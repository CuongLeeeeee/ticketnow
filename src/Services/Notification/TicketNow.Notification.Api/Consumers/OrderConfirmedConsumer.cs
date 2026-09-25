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
                "Đặt vé thành công", $"Đơn hàng {amount} đã được xác nhận.", DateTimeOffset.UtcNow),
            "Vé của bạn đã được xác nhận",
            EmailTemplates.Layout("Đặt vé thành công", $"""
                <p>Đơn hàng của bạn đã được thanh toán và xác nhận.</p>
                <p><strong>Mã đơn:</strong> {m.OrderId}<br>
                   <strong>Tổng tiền:</strong> {amount}</p>
                <p>Vui lòng mang mã đơn tới cổng soát vé.</p>
                """),
            context.CancellationToken);
    }
}