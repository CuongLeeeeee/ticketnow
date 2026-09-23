using Microsoft.EntityFrameworkCore;
using TicketNow.Booking.Domain;
using TicketNow.Booking.Infrastructure.Persistence;
using TicketNow.Contracts.Booking;

namespace TicketNow.Booking.Api.BackgroundServices;

public sealed class OrderExpirySweeper(IServiceScopeFactory scopes, ILogger<OrderExpirySweeper> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                var count = await SweepAsync(ct);
                if (count > 0) log.LogInformation("Đã chuyển {Count} đơn sang Expired", count);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "Quét đơn hết hạn thất bại");
            }
        }
    }

    private async Task<int> SweepAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BookingDbContext>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Lock these lines: if PaymentSucceeded arrives simultaneously, it must wait,
        // then see that the status is already Expired => proceed to the refund branch.
        var expired = await db.Orders
            .FromSql($"""
                SELECT * FROM orders
                WHERE status = 'AwaitingPayment' AND expires_at < now()
                ORDER BY expires_at
                LIMIT 100
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(ct);

        if (expired.Count == 0) return 0;

        var ids = expired.Select(o => o.Id).ToList();
        var items = await db.OrderItems.AsNoTracking().Where(i => ids.Contains(i.OrderId)).ToListAsync(ct);

        foreach (var order in expired)
        {
            order.Status = OrderStatus.Expired;
            db.OutboxMessages.Add(OutboxMessage.From(new OrderReleased(
                order.Id, order.UserId, ReleaseReasons.Expired,
                items.Where(i => i.OrderId == order.Id)
                     .Select(i => new OrderLine(i.TicketTypeId, i.Quantity, i.UnitPrice))
                     .ToList())));
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return expired.Count;
    }
}