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
                "Đã hoàn tiền", $"Số tiền {amount} đang được hoàn về tài khoản của bạn.", DateTimeOffset.UtcNow),
            "Thông báo hoàn tiền",
            EmailTemplates.Layout("Đã hoàn tiền", $"""
                <p>Đơn hàng không thể hoàn tất nên chúng tôi đã hoàn lại tiền.</p>
                <p><strong>Mã đơn:</strong> {m.OrderId}<br>
                   <strong>Số tiền:</strong> {amount}</p>
                <p>Tiền về tài khoản trong 3–5 ngày làm việc.</p>
                """, "#BA7517"),
            context.CancellationToken);
    }
}