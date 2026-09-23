using MassTransit;
using Microsoft.EntityFrameworkCore;
using TicketNow.BuildingBlocks.Messaging;
using TicketNow.Contracts.Booking;
using TicketNow.Contracts.Payment;
using TicketNow.Payment.Api.Domain;
using TicketNow.Payment.Api.Persistence;

namespace TicketNow.Payment.Api.Consumers;

public sealed class PaymentRefundRequestedConsumer(PaymentDbContext db, ILogger<PaymentRefundRequestedConsumer> log)
    : IConsumer<PaymentRefundRequested>
{
    public async Task Consume(ConsumeContext<PaymentRefundRequested> context)
    {
        var m = context.Message;
        var ct = context.CancellationToken;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.TryRegisterInboxAsync(context.MessageId!.Value, nameof(PaymentRefundRequestedConsumer), ct))
            return;

        var payment = await db.Payments.FirstOrDefaultAsync(
            p => p.OrderId == m.OrderId && p.Status == PaymentStatus.Succeeded, ct);

        if (payment is not null)
        {
            payment.Status = PaymentStatus.Refunded;
            payment.UpdatedAt = DateTimeOffset.UtcNow;
            db.OutboxMessages.Add(OutboxMessage.From(new PaymentRefunded(
                payment.Id, payment.OrderId, payment.UserId, payment.Amount, payment.Currency)));
            log.LogWarning("Đã hoàn {Amount} {Currency} cho order {OrderId}", payment.Amount, payment.Currency, m.OrderId);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}