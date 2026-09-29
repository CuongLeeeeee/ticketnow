using MassTransit;
using Microsoft.EntityFrameworkCore;
using TicketNow.Booking.Domain;
using TicketNow.Booking.Infrastructure.Persistence;
using TicketNow.BuildingBlocks.Messaging;
using TicketNow.Contracts.Booking;
using TicketNow.Contracts.Payment;

namespace TicketNow.Booking.Api.Consumers;

public sealed class PaymentSucceededConsumer(BookingDbContext db, ILogger<PaymentSucceededConsumer> log)
    : IConsumer<PaymentSucceeded>
{
    public async Task Consume(ConsumeContext<PaymentSucceeded> context)
    {
        var m = context.Message;
        var ct = context.CancellationToken;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.TryRegisterInboxAsync(context.MessageId!.Value, nameof(PaymentSucceededConsumer), ct))
            return;

        var confirmed = await db.Orders
            .Where(o => o.Id == m.OrderId && o.Status == OrderStatus.AwaitingPayment)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Confirmed), ct);

        if (confirmed == 1)
        {
            var order = await db.Orders.AsNoTracking().Include(o => o.Items).FirstAsync(o => o.Id == m.OrderId, ct);

            // CHECK (sold <= total) acts as a database-level safeguard in case a bug slips past Redis.
            foreach (var item in order.Items)
                await db.Inventory
                    .Where(i => i.TicketTypeId == item.TicketTypeId)
                    .ExecuteUpdateAsync(s => s.SetProperty(i => i.Sold, i => i.Sold + item.Quantity), ct);

            db.OutboxMessages.Add(OutboxMessage.From(new OrderConfirmed(
                order.Id, order.UserId, order.EventId, order.TotalAmount, order.Currency)));
        }
        else
        {
            // Funds received but the order has expired/been cancelled => Saga reconciliation step: refund.
            log.LogWarning("Order {OrderId} was no longer awaiting payment when the payment arrived, requesting a refund", m.OrderId);
            db.OutboxMessages.Add(OutboxMessage.From(
                new PaymentRefundRequested(m.OrderId, m.UserId, "OrderNoLongerAwaitingPayment")));
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}