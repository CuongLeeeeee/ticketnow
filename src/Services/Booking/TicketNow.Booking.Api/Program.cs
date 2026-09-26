using MassTransit;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;
using TicketNow.Booking.Api.Application;
using TicketNow.Booking.Api.BackgroundServices;
using TicketNow.Booking.Domain;
using TicketNow.Booking.Infrastructure.Persistence;
using TicketNow.Booking.Infrastructure.Redis;
using TicketNow.BuildingBlocks.Auth;
using TicketNow.BuildingBlocks.Messaging;
using TicketNow.Contracts.Booking;
using TicketNow.Contracts.Catalog;
using TicketNow.BuildingBlocks.Observability;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<BookingDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Postgres"))
     .UseSnakeCaseNamingConvention());

builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ =>
    StackExchange.Redis.ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("Redis")!));
builder.Services.AddSingleton<InventoryStore>();
builder.Services.AddSingleton<IdempotencyStore>();
builder.Services.AddHostedService<InventoryRebuilder>();
builder.Services.AddTicketNowMessaging(builder.Configuration, "booking",
    x => x.AddConsumers(typeof(Program).Assembly));
builder.Services.AddOutboxPublisher<BookingDbContext>();
builder.Services.AddHostedService<OrderExpirySweeper>();
builder.Services.AddTicketNowJwt(builder.Configuration);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();
builder.Services.AddSingleton<WaitingRoomStore>();
builder.Services.AddHostedService<WaitingRoomAdmitter>();
builder.Services.AddTicketNowTelemetry(builder.Configuration, "booking", withRedis: true);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<BookingDbContext>().Database.MigrateAsync();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health");

var holdMinutes = builder.Configuration.GetValue("Booking:HoldMinutes", 10);
var webJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);

// ================= ORDERS =================
var orders = app.MapGroup("/api/orders").RequireAuthorization("authenticated");

orders.MapPost("", async (
    PlaceOrderRequest req,
    HttpContext http,
    ClaimsPrincipal principal,
    BookingDbContext db,
    InventoryStore inventory,
    IdempotencyStore idempotency,
    ILogger<Program> log,
    WaitingRoomStore waitingRoom) =>
{
    var userId = GetUserId(principal);

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
    var order = new TicketNow.Booking.Domain.Order
    {
        UserId = userId,
        EventId = first.EventId,
        Currency = first.Currency,
        IdempotencyKey = idemKey,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(holdMinutes),
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
    var body = JsonSerializer.Serialize(ToDto(order), webJson);
    await idempotency.CompleteAsync(userId, idemKey, new StoredResponse(StatusCodes.Status201Created, body));
    return Results.Content(body, "application/json", statusCode: StatusCodes.Status201Created);
});

orders.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal principal, BookingDbContext db) =>
{
    var order = await db.Orders.AsNoTracking().Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == id);

    // Return 404 instead of 403 so we don't reveal whether another user's order exists
    if (order is null || (order.UserId != GetUserId(principal) && !principal.IsInRole("Admin")))
        return Results.NotFound();

    return Results.Ok(ToDto(order));
});

orders.MapGet("/me", async (ClaimsPrincipal principal, BookingDbContext db) =>
{
    var userId = GetUserId(principal);
    var list = await db.Orders.AsNoTracking().Include(o => o.Items)
        .Where(o => o.UserId == userId)
        .OrderByDescending(o => o.CreatedAt)
        .Take(50)
        .ToListAsync();
    return Results.Ok(list.Select(ToDto));
});

orders.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal principal, BookingDbContext db) =>
{
    var userId = GetUserId(principal);
    if (!await db.Orders.AnyAsync(o => o.Id == id && o.UserId == userId))
        return Results.NotFound();

    await using var tx = await db.Database.BeginTransactionAsync();
    if (!await OrderReleaser.TryReleaseAsync(db, id, OrderStatus.Cancelled, ReleaseReasons.UserCancelled, default))
        return Results.Conflict("Only orders awaiting payment can be cancelled");

    await db.SaveChangesAsync();
    await tx.CommitAsync();

    return Results.NoContent();
});

// ================= ADMIN INVENTORY =================
var adminInventory = app.MapGroup("/api/admin/inventory").RequireAuthorization("admin");

adminInventory.MapGet("/{ticketTypeId:guid}", async (Guid ticketTypeId, BookingDbContext db, InventoryStore inventory) =>
{
    var item = await db.Inventory.AsNoTracking().FirstOrDefaultAsync(x => x.TicketTypeId == ticketTypeId);
    if (item is null) return Results.NotFound();

    var held = await db.OrderItems
        .Where(i => i.TicketTypeId == ticketTypeId &&
                    (i.Order.Status == OrderStatus.AwaitingPayment || i.Order.Status == OrderStatus.Confirmed))
        .SumAsync(i => (int?)i.Quantity) ?? 0;

    var inRedis = await inventory.GetAvailableAsync(ticketTypeId);

    return Results.Ok(new
    {
        item.Name,
        item.Total,
        HeldOrSold = held,
        AvailableInDb = item.Total - held,
        AvailableInRedis = inRedis,
        InSync = inRedis == item.Total - held
    });
});

var waitingRoomApi = app.MapGroup("/api/waiting-room").RequireAuthorization("authenticated");

waitingRoomApi.MapPost("/{eventId:guid}/join", async (Guid eventId, ClaimsPrincipal principal, WaitingRoomStore store) =>
{
    var cfg = await store.GetConfigAsync(eventId);
    if (cfg is not { Enabled: true })
        return Results.Ok(new { state = "Disabled", position = 0, queueLength = 0 });

    var s = await store.JoinAsync(eventId, GetUserId(principal));
    return Results.Ok(new { state = s.State.ToString(), position = s.Position, queueLength = s.QueueLength });
});

waitingRoomApi.MapGet("/{eventId:guid}/status", async (Guid eventId, ClaimsPrincipal principal, WaitingRoomStore store) =>
{
    var cfg = await store.GetConfigAsync(eventId);
    if (cfg is not { Enabled: true })
        return Results.Ok(new { state = "Disabled", position = 0, queueLength = 0, etaSeconds = 0 });

    var s = await store.StatusAsync(eventId, GetUserId(principal));
    var eta = s.State == WaitingRoomState.Waiting && cfg.AdmitPerTick > 0
        ? (long)Math.Ceiling((double)s.Position / cfg.AdmitPerTick)
        : 0;

    return Results.Ok(new { state = s.State.ToString(), position = s.Position, queueLength = s.QueueLength, etaSeconds = eta });
});

var adminWaitingRoom = app.MapGroup("/api/admin/waiting-room").RequireAuthorization("admin");

adminWaitingRoom.MapPost("/{eventId:guid}", async (Guid eventId, WaitingRoomSettings req, WaitingRoomStore store) =>
{
    if (req.Enabled) await store.EnableAsync(eventId, Math.Max(1, req.AdmitPerTick), Math.Max(30, req.AdmitTtlSeconds));
    else await store.DisableAsync(eventId);
    return Results.Ok(await store.GetConfigAsync(eventId));
});

app.Run();

// ================= HELPERS =================
static Guid GetUserId(ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue("sub")!);

static string? ValidateShape(PlaceOrderRequest req)
{
    if (req.Items is null || req.Items.Count is 0 or > 4)
        return "An order must contain 1 to 4 ticket types";
    if (req.Items.Any(i => i.Quantity is <= 0 or > 10))
        return "Quantity per ticket type must be between 1 and 10";
    if (req.Items.Select(i => i.TicketTypeId).Distinct().Count() != req.Items.Count)
        return "Each ticket type may appear only once";
    return null;
}

static OrderDto ToDto(Order o) => new(
    o.Id, o.EventId, o.Status.ToString(), o.TotalAmount, o.Currency, o.ExpiresAt, o.CreatedAt,
    o.Items.Select(i => new OrderItemDto(i.TicketTypeId, i.Quantity, i.UnitPrice)).ToList());

record PlaceOrderRequest(List<PlaceOrderItem> Items);
record PlaceOrderItem(Guid TicketTypeId, int Quantity);
record OrderItemDto(Guid TicketTypeId, int Quantity, decimal UnitPrice);
record OrderDto(Guid Id, Guid EventId, string Status, decimal TotalAmount, string Currency,
    DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt, List<OrderItemDto> Items);
record WaitingRoomSettings(bool Enabled, int AdmitPerTick, int AdmitTtlSeconds);
public partial class Program;