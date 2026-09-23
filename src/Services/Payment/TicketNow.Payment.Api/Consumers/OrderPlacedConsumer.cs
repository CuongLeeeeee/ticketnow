using MassTransit;
using TicketNow.BuildingBlocks.Messaging;
using TicketNow.Contracts.Booking;
using TicketNow.Payment.Api.Domain;
using TicketNow.Payment.Api.Persistence;

namespace TicketNow.Payment.Api.Consumers;

public sealed class OrderPlacedConsumer(PaymentDbContext db) : IConsumer<OrderPlaced>
{
    public async Task Consume(ConsumeContext<OrderPlaced> context)
    {
        var m = context.Message;
        var ct = context.CancellationToken;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.TryRegisterInboxAsync(context.MessageId!.Value, nameof(OrderPlacedConsumer), ct))
            return;

        db.Payments.Add(new PaymentTransaction
        {
            OrderId = m.OrderId,
            UserId = m.UserId,
            Amount = m.TotalAmount,
            Currency = m.Currency,
            ExpiresAt = m.ExpiresAt
        });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}