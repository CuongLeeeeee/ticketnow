using MassTransit;
using TicketNow.Booking.Api.Application;
using TicketNow.Booking.Domain;
using TicketNow.Booking.Infrastructure.Persistence;
using TicketNow.BuildingBlocks.Messaging;
using TicketNow.Contracts.Booking;
using TicketNow.Contracts.Payment;

namespace TicketNow.Booking.Api.Consumers;

public sealed class PaymentFailedConsumer(BookingDbContext db) : IConsumer<PaymentFailed>
{
    public async Task Consume(ConsumeContext<PaymentFailed> context)
    {
        var ct = context.CancellationToken;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.TryRegisterInboxAsync(context.MessageId!.Value, nameof(PaymentFailedConsumer), ct))
            return;

        await OrderReleaser.TryReleaseAsync(
            db, context.Message.OrderId, OrderStatus.PaymentFailed, ReleaseReasons.PaymentFailed, ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}