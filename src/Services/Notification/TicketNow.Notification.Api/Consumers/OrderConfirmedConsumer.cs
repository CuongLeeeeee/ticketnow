using MassTransit;
using TicketNow.Contracts.Booking;
using TicketNow.Notification.Api.Services;

namespace TicketNow.Notification.Api.Consumers;

public sealed class OrderConfirmedConsumer(NotificationDispatcher dispatcher) : IConsumer<OrderConfirmed>
{
    public async Task Consume(ConsumeContext<OrderConfirmed> context)
    {
        var m = context.Message;
        var amount = EmailTemplates.Money(m.TotalAmount, m.Currency);

        await dispatcher.SendAsync(
            context.MessageId!.Value, m.UserId,
            new NotificationPayload("OrderConfirmed", m.OrderId,
                "Booking confirmed", $"Your {amount} order has been confirmed.", DateTimeOffset.UtcNow),
            "Your tickets are confirmed",
            EmailTemplates.Layout("Booking confirmed", $"""
                <p>Your order has been paid and confirmed.</p>
                <p><strong>Order ID:</strong> {m.OrderId}<br>
                   <strong>Total:</strong> {amount}</p>
                <p>Please show your order ID at the entrance.</p>
                """),
            context.CancellationToken);
    }
}