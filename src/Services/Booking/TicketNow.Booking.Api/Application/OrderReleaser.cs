using Microsoft.EntityFrameworkCore;
using TicketNow.Booking.Domain;
using TicketNow.Booking.Infrastructure.Persistence;
using TicketNow.Contracts.Booking;

namespace TicketNow.Booking.Api.Application;

public static class OrderReleaser
{
    /// <summary>
    /// AwaitingPayment -> target, kèm OrderReleased vào outbox.
    /// Người gọi tự mở transaction và SaveChanges.
    /// </summary>
    public static async Task<bool> TryReleaseAsync(
        BookingDbContext db, Guid orderId, OrderStatus target, string reason, CancellationToken ct)
    {
        var changed = await db.Orders
            .Where(o => o.Id == orderId && o.Status == OrderStatus.AwaitingPayment)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, target), ct);

        if (changed == 0) return false;

        var order = await db.Orders.AsNoTracking().Include(o => o.Items).FirstAsync(o => o.Id == orderId, ct);
        db.OutboxMessages.Add(OutboxMessage.From(new OrderReleased(
            order.Id, order.UserId, reason,
            order.Items.Select(i => new OrderLine(i.TicketTypeId, i.Quantity, i.UnitPrice)).ToList())));
        return true;
    }
}