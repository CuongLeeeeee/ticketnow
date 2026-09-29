using MassTransit;
using TicketNow.Contracts.Payment;
using TicketNow.Notification.Api.Services;

namespace TicketNow.Notification.Api.Consumers;

public sealed class PaymentRefundedConsumer(NotificationDispatcher dispatcher) : IConsumer<PaymentRefunded>
{
    public async Task Consume(ConsumeContext<PaymentRefunded> context)
    {
        var m = context.Message;
        var amount = EmailTemplates.Money(m.Amount, m.Currency);

        await dispatcher.SendAsync(
            context.MessageId!.Value, m.UserId,
            new NotificationPayload("PaymentRefunded", m.OrderId,
                "Refund issued", $"{amount} is being refunded to your account.", DateTimeOffset.UtcNow),
            "Refund notice",
            EmailTemplates.Layout("Refund issued", $"""
                <p>Your order could not be completed, so we have refunded your payment.</p>
                <p><strong>Order ID:</strong> {m.OrderId}<br>
                   <strong>Amount:</strong> {amount}</p>
                <p>The funds should reach your account within 3–5 business days.</p>
                """, "#BA7517"),
            context.CancellationToken);
    }
}