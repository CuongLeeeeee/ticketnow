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
    ILogger<Program> log) =>
{
    var userId = GetUserId(principal);

    // ---- 1. Idempotency ----
    var idemKey = http.Request.Headers["Idempotency-Key"].ToString();
    if (string.IsNullOrWhiteSpace(idemKey) || idemKey.Length > 64)
        return Results.BadRequest("Thiếu header Idempotency-Key (tối đa 64 ký tự)");

    var (state, stored) = await idempotency.TryBeginAsync(userId, idemKey);
    if (state == IdempotencyState.InProgress)
        return Results.Conflict("Request với Idempotency-Key này đang được xử lý");
    if (state == IdempotencyState.Completed)
    {
        http.Response.Headers["Idempotent-Replayed"] = "true";
        return Results.Content(stored!.Body, "application/json", statusCode: stored.StatusCode);
    }

    // Từ đây, mọi nhánh thất bại phải Abort để client được phép thử lại
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
        return await Fail(Results.BadRequest("Loại vé không tồn tại hoặc thuộc nhiều sự kiện khác nhau"));

    if (stock.Values.Any(s => s.SalesStartAt > DateTimeOffset.UtcNow))
        return await Fail(Results.Conflict("Chưa tới giờ mở bán"));

    // ---- 3. Giữ vé trên Redis (nguyên tử) ----
    var lines = req.Items
        .Select(i => (i.TicketTypeId, i.Quantity, stock[i.TicketTypeId].MaxPerUser))
        .ToList();

    var (result, failed) = await inventory.ReserveAsync(userId, lines);
    if (result != ReserveResult.Ok)
    {
        var tt = stock[req.Items[failed].TicketTypeId];
        return await Fail(result switch
        {
            ReserveResult.SoldOut => Results.Conflict($"Vé {tt.Name} đã hết hoặc không đủ số lượng"),
            ReserveResult.LimitExceeded => Results.Conflict($"Mỗi người chỉ được giữ tối đa {tt.MaxPerUser} vé {tt.Name}"),
            _ => Results.Problem($"Tồn kho {tt.Name} chưa được khởi tạo", statusCode: 503)
        });
    }

    // ---- 4. Ghi order + outbox trong CÙNG một transaction ----
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
        // Redis đã trừ vé nhưng DB không ghi được => phải trả vé lại, nếu không vé sẽ "bốc hơi"
        log.LogError(ex, "Ghi order thất bại, hoàn vé trên Redis cho user {UserId}", userId);
        await inventory.ReleaseOnceAsync(order.Id, userId, req.Items.Select(i => (i.TicketTypeId, i.Quantity)));
        await idempotency.AbortAsync(userId, idemKey);
        throw;
    }

    // ---- 5. Lưu kết quả để lần gửi lại trả về y hệt ----
    var body = JsonSerializer.Serialize(ToDto(order), webJson);
    await idempotency.CompleteAsync(userId, idemKey, new StoredResponse(StatusCodes.Status201Created, body));
    return Results.Content(body, "application/json", statusCode: StatusCodes.Status201Created);
});

orders.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal principal, BookingDbContext db) =>
{
    var order = await db.Orders.AsNoTracking().Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == id);

    // Trả 404 thay vì 403 để không tiết lộ đơn của người khác có tồn tại hay không
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
        return Results.Conflict("Chỉ hủy được đơn đang chờ thanh toán");

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

app.Run();

// ================= HELPERS =================
static Guid GetUserId(ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue("sub")!);

static string? ValidateShape(PlaceOrderRequest req)
{
    if (req.Items is null || req.Items.Count is 0 or > 4)
        return "Đơn hàng phải có từ 1 đến 4 loại vé";
    if (req.Items.Any(i => i.Quantity is <= 0 or > 10))
        return "Số lượng mỗi loại vé từ 1 đến 10";
    if (req.Items.Select(i => i.TicketTypeId).Distinct().Count() != req.Items.Count)
        return "Mỗi loại vé chỉ được xuất hiện một lần";
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