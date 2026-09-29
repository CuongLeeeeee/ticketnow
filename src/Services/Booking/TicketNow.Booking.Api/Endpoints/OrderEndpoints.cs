using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TicketNow.Booking.Api.Application;
using TicketNow.Booking.Domain;
using TicketNow.Booking.Infrastructure.Persistence;
using TicketNow.Booking.Infrastructure.Redis;
using TicketNow.Contracts.Booking;

namespace TicketNow.Booking.Api.Endpoints;

public static class OrderEndpoints
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var orders = app.MapGroup("/api/orders").RequireAuthorization("authenticated");

        orders.MapPost("", PlaceOrder);
        orders.MapGet("/{id:guid}", GetOrder);
        orders.MapGet("/me", GetMyOrders);
        orders.MapDelete("/{id:guid}", CancelOrder);

        return app;
    }

    private static async Task<IResult> PlaceOrder(
        PlaceOrderRequest req,
        HttpContext http,
        ClaimsPrincipal principal,
        BookingDbContext db,
        InventoryStore inventory,
        IdempotencyStore idempotency,
        WaitingRoomStore waitingRoom,
        IConfiguration config,
        ILogger<Program> log)
    {
        var userId = principal.GetUserId();

        // ---- 1. Idempotency ----
        var idemKey = http.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(idemKey) || idemKey.Length > 64)
            return Results.BadRequest("Missing Idempotency-Key header (max 64 characters)");

        var (state, stored) = await idempotency.TryBeginAsync(userId, idemKey);
        if (state == IdempotencyState.InProgress)
            return Results.Conflict("A request with this Idempotency-Key is already being processed");
        if (state == IdempotencyState.Completed)
        {
            http.Response.Headers["Idempotent-Replayed"] = "true";
            return Results.Content(stored!.Body, "application/json", statusCode: stored.StatusCode);
        }

        // From here on, every failure path must Abort so the client can retry with the same key
        async Task<IResult> Fail(IResult result)
        {
            await idempotency.AbortAsync(userId, idemKey);
            return result;
        }

        // ---- 2. Validate ----
        var shapeError = ValidateShape(req);
        if (shapeError is not null) return await Fail(Results.BadRequest(shapeError));

        var ids = req.Items.Select(i => i.TicketTypeId).ToList();
        var stock = await db.Inventory.AsNoTracking()
            .Where(x => ids.Contains(x.TicketTypeId))
            .ToDictionaryAsync(x => x.TicketTypeId);

        if (stock.Count != ids.Count || stock.Values.Select(s => s.EventId).Distinct().Count() != 1)
            return await Fail(Results.BadRequest("Ticket type not found, or ticket types belong to different events"));

        if (stock.Values.Any(s => s.SalesStartAt > DateTimeOffset.UtcNow))
            return await Fail(Results.Conflict("Sales have not started yet"));

        var eventId = stock.Values.First().EventId;
        var wrConfig = await waitingRoom.GetConfigAsync(eventId);
        if (wrConfig is { Enabled: true } && !await waitingRoom.IsAdmittedAsync(eventId, userId))
        {
            var status = await waitingRoom.StatusAsync(eventId, userId);
            return await Fail(Results.Json(new
            {
                error = "You must join the waiting room before booking",
                state = status.State.ToString(),
                position = status.Position,
                queueLength = status.QueueLength
            }, statusCode: StatusCodes.Status403Forbidden));
        }

        // ---- 3. Reserve tickets in Redis (atomic) ----
        var lines = req.Items
            .Select(i => (i.TicketTypeId, i.Quantity, stock[i.TicketTypeId].MaxPerUser))
            .ToList();

        var (result, failed) = await inventory.ReserveAsync(userId, lines);
        if (result != ReserveResult.Ok)
        {
            var tt = stock[req.Items[failed].TicketTypeId];
            return await Fail(result switch
            {
                ReserveResult.SoldOut => Results.Conflict($"{tt.Name} is sold out or not enough tickets are left"),
                ReserveResult.LimitExceeded => Results.Conflict($"Each user can hold at most {tt.MaxPerUser} {tt.Name} tickets"),
                _ => Results.Problem($"Inventory for {tt.Name} is not initialized", statusCode: 503)
            });
        }

        // ---- 4. Save order + outbox in the SAME transaction ----
        var first = stock.Values.First();
        var order = new Order
        {
            UserId = userId,
            EventId = first.EventId,
            Currency = first.Currency,
            IdempotencyKey = idemKey,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(config.GetValue("Booking:HoldMinutes", 10)),
            Items = req.Items.Select(i => new OrderItem
            {
                TicketTypeId = i.TicketTypeId,
                Quantity = i.Quantity,
                UnitPrice = stock[i.TicketTypeId].Price
            }).ToList()
        };
        order.TotalAmount = order.Items.Sum(i => i.UnitPrice * i.Quantity);

        db.Orders.Add(order);
        db.OutboxMessages.Add(new OutboxMessage
        {
            Type = nameof(OrderPlaced),
            Payload = JsonSerializer.Serialize(new OrderPlaced(
                order.Id, order.UserId, order.EventId, order.TotalAmount, order.Currency, order.ExpiresAt,
                order.Items.Select(i => new OrderLine(i.TicketTypeId, i.Quantity, i.UnitPrice)).ToList()))
        });

        try
        {
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Redis already deducted the tickets but the DB write failed: give them back or they are lost
            log.LogError(ex, "Saving order failed, releasing Redis reservation for user {UserId}", userId);
            await inventory.ReleaseOnceAsync(order.Id, userId, req.Items.Select(i => (i.TicketTypeId, i.Quantity)));
            await idempotency.AbortAsync(userId, idemKey);
            throw;
        }

        // ---- 5. Store the response so a retry returns exactly the same result ----
        var body = JsonSerializer.Serialize(ToDto(order), WebJson);
        await idempotency.CompleteAsync(userId, idemKey, new StoredResponse(StatusCodes.Status201Created, body));
        return Results.Content(body, "application/json", statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> GetOrder(Guid id, ClaimsPrincipal principal, BookingDbContext db)
    {
        var order = await db.Orders.AsNoTracking().Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == id);

        // Return 404 instead of 403 so we don't reveal whether another user's order exists
        if (order is null || (order.UserId != principal.GetUserId() && !principal.IsInRole("Admin")))
            return Results.NotFound();

        return Results.Ok(ToDto(order));
    }

    private static async Task<IResult> GetMyOrders(ClaimsPrincipal principal, BookingDbContext db)
    {
        var userId = principal.GetUserId();
        var list = await db.Orders.AsNoTracking().Include(o => o.Items)
            .Where(o => o.UserId == userId)
            .OrderByDescending(o => o.CreatedAt)
            .Take(50)
            .ToListAsync();
        return Results.Ok(list.Select(ToDto));
    }

    private static async Task<IResult> CancelOrder(Guid id, ClaimsPrincipal principal, BookingDbContext db)
    {
        var userId = principal.GetUserId();
        if (!await db.Orders.AnyAsync(o => o.Id == id && o.UserId == userId))
            return Results.NotFound();

        await using var tx = await db.Database.BeginTransactionAsync();
        if (!await OrderReleaser.TryReleaseAsync(db, id, OrderStatus.Cancelled, ReleaseReasons.UserCancelled, default))
            return Results.Conflict("Only orders awaiting payment can be cancelled");

        await db.SaveChangesAsync();
        await tx.CommitAsync();

        return Results.NoContent();
    }

    private static string? ValidateShape(PlaceOrderRequest req)
    {
        if (req.Items is null || req.Items.Count is 0 or > 4)
            return "An order must contain 1 to 4 ticket types";
        if (req.Items.Any(i => i.Quantity is <= 0 or > 10))
            return "Quantity per ticket type must be between 1 and 10";
        if (req.Items.Select(i => i.TicketTypeId).Distinct().Count() != req.Items.Count)
            return "Each ticket type may appear only once";
        return null;
    }

    private static OrderDto ToDto(Order o) => new(
        o.Id, o.EventId, o.Status.ToString(), o.TotalAmount, o.Currency, o.ExpiresAt, o.CreatedAt,
        o.Items.Select(i => new OrderItemDto(i.TicketTypeId, i.Quantity, i.UnitPrice)).ToList());
}
