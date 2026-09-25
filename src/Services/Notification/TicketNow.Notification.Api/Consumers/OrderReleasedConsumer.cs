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
            ReleaseReasons.Expired => ("Đơn hàng đã hết hạn",
                "Bạn không hoàn tất thanh toán trong thời gian giữ vé. Vé đã được trả lại kho."),
            ReleaseReasons.PaymentFailed => ("Thanh toán không thành công",
                "Giao dịch bị từ chối nên đơn hàng đã được hủy. Bạn có thể đặt lại nếu vé còn."),
            _ => ("Đơn hàng đã được hủy", "Đơn hàng của bạn đã được hủy theo yêu cầu.")
        };

        await dispatcher.SendAsync(
            context.MessageId!.Value, m.UserId,
            new NotificationPayload("OrderReleased", m.OrderId, title, body, DateTimeOffset.UtcNow),
            title,
            EmailTemplates.Layout(title, $"<p>{body}</p><p><strong>Mã đơn:</strong> {m.OrderId}</p>", "#D85A30"),
            context.CancellationToken);
    }
}