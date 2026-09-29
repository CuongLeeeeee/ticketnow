using MassTransit;
using TicketNow.Contracts.Booking;
using TicketNow.Notification.Api.Services;

namespace TicketNow.Notification.Api.Consumers;

public sealed class OrderReleasedConsumer(NotificationDispatcher dispatcher) : IConsumer<OrderReleased>
{
    public async Task Consume(ConsumeContext<OrderReleased> context)
    {
        var m = context.Message;

        var (title, body) = m.Reason switch
        {
            ReleaseReasons.Expired => ("Order expired",
                "Payment was not completed within the hold period. The tickets have been released."),
            ReleaseReasons.PaymentFailed => ("Payment failed",
                "The transaction was declined, so your order was cancelled. You can book again if tickets are still available."),
            _ => ("Order cancelled", "Your order has been cancelled as requested.")
        };

        await dispatcher.SendAsync(
            context.MessageId!.Value, m.UserId,
            new NotificationPayload("OrderReleased", m.OrderId, title, body, DateTimeOffset.UtcNow),
            title,
            EmailTemplates.Layout(title, $"<p>{body}</p><p><strong>Order ID:</strong> {m.OrderId}</p>", "#D85A30"),
            context.CancellationToken);
    }
}