using MassTransit;
using Microsoft.EntityFrameworkCore;
using TicketNow.Contracts.Booking;
using TicketNow.Payment.Api.Domain;
using TicketNow.Payment.Api.Persistence;

namespace TicketNow.Payment.Api.Consumers;

public sealed class OrderReleasedConsumer(PaymentDbContext db) : IConsumer<OrderReleased>
{
    public async Task Consume(ConsumeContext<OrderReleased> context)
    {
        // Natural idempotency: applies only to the Pending -> Cancelled transition; a second retry will not match any records.
        await db.Payments
            .Where(p => p.OrderId == context.Message.OrderId && p.Status == PaymentStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, PaymentStatus.Cancelled)
                .SetProperty(p => p.UpdatedAt, DateTimeOffset.UtcNow),
                context.CancellationToken);
    }
}