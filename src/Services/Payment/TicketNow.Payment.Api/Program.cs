using MassTransit;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using TicketNow.BuildingBlocks.Auth;
using TicketNow.BuildingBlocks.Messaging;
using TicketNow.Contracts.Payment;
using TicketNow.Payment.Api.Domain;
using TicketNow.Payment.Api.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<PaymentDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Postgres"))
     .UseSnakeCaseNamingConvention());

builder.Services.AddTicketNowJwt(builder.Configuration);
builder.Services.AddTicketNowMessaging(builder.Configuration, "payment",
    x => x.AddConsumers(typeof(Program).Assembly));
builder.Services.AddOutboxPublisher<PaymentDbContext>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<PaymentDbContext>().Database.MigrateAsync();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health");

var payments = app.MapGroup("/api/payments").RequireAuthorization("authenticated");

payments.MapGet("/order/{orderId:guid}", async (Guid orderId, ClaimsPrincipal principal, PaymentDbContext db) =>
{
    var userId = GetUserId(principal);
    var p = await db.Payments.AsNoTracking()
        .FirstOrDefaultAsync(x => x.OrderId == orderId && x.UserId == userId);

    return p is null
        ? Results.NotFound()
        : Results.Ok(new { p.Id, p.OrderId, Status = p.Status.ToString(), p.Amount, p.Currency, p.ProviderRef, p.FailureReason, p.ExpiresAt });
});

// Giả lập cổng thanh toán. Hệ thống thật: redirect sang cổng, cổng gọi webhook về.
payments.MapPost("/{orderId:guid}/pay", async (
    Guid orderId, PayRequest? req, ClaimsPrincipal principal, PaymentDbContext db, IConfiguration config) =>
{
    var userId = GetUserId(principal);
    var payment = await db.Payments.AsNoTracking()
        .FirstOrDefaultAsync(p => p.OrderId == orderId && p.UserId == userId);

    // Eventual consistency: OrderPlaced có thể chưa tới kịp
    if (payment is null)
        return Results.NotFound("Chưa có thông tin thanh toán. Nếu vừa đặt vé, thử lại sau 1–2 giây.");
    if (payment.Status != PaymentStatus.Pending)
        return Results.Conflict($"Thanh toán đang ở trạng thái {payment.Status}");
    if (payment.ExpiresAt <= DateTimeOffset.UtcNow)
        return Results.Conflict("Đơn đã hết thời gian giữ vé");

    var failureRate = config.GetValue("Payment:SimulatedFailureRate", 0.2);
    var success = req?.Simulate switch
    {
        "success" => true,
        "fail" => false,
        _ => Random.Shared.NextDouble() >= failureRate
    };
    await Task.Delay(Random.Shared.Next(200, 800));

    var newStatus = success ? PaymentStatus.Succeeded : PaymentStatus.Failed;
    var providerRef = success ? $"SIM-{Guid.NewGuid():N}"[..16] : null;
    var failureReason = success ? null : "Ngân hàng từ chối giao dịch (giả lập)";

    await using var tx = await db.Database.BeginTransactionAsync();

    // Prevent double-clicking "Pay": only one request can transition the status to Pending.
    var changed = await db.Payments
        .Where(p => p.Id == payment.Id && p.Status == PaymentStatus.Pending)
        .ExecuteUpdateAsync(s => s
            .SetProperty(p => p.Status, newStatus)
            .SetProperty(p => p.ProviderRef, providerRef)
            .SetProperty(p => p.FailureReason, failureReason)
            .SetProperty(p => p.UpdatedAt, DateTimeOffset.UtcNow));

    if (changed == 0)
        return Results.Conflict("Thanh toán vừa được xử lý hoặc đã bị hủy");

    db.OutboxMessages.Add(success
        ? OutboxMessage.From(new PaymentSucceeded(payment.Id, payment.OrderId, payment.UserId, payment.Amount, payment.Currency, providerRef!))
        : OutboxMessage.From(new PaymentFailed(payment.Id, payment.OrderId, payment.UserId, failureReason!)));

    await db.SaveChangesAsync();
    await tx.CommitAsync();

    return Results.Ok(new { payment.OrderId, Status = newStatus.ToString(), ProviderRef = providerRef, FailureReason = failureReason });
});

app.Run();

static Guid GetUserId(ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue("sub")!);

record PayRequest(string? Simulate);