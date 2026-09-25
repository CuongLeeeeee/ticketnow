using MassTransit;
using TicketNow.Contracts.Identity;
using TicketNow.Notification.Api.Services;

namespace TicketNow.Notification.Api.Consumers;

public sealed class UserRegisteredConsumer(UserDirectory directory, EmailSender email)
    : IConsumer<UserRegistered>
{
    public async Task Consume(ConsumeContext<UserRegistered> context)
    {
        var m = context.Message;

        // Ghi đè là idempotent sẵn, không cần dedupe
        await directory.SaveAsync(m.UserId, new UserContact(m.Email, m.FullName));

        await email.SendAsync(m.Email, m.FullName, "Chào mừng tới TicketNow",
            EmailTemplates.Layout("Tài khoản đã sẵn sàng",
                $"<p>Xin chào <strong>{m.FullName}</strong>,</p><p>Tài khoản của bạn đã được tạo. Chúc bạn săn vé thành công.</p>"),
            context.CancellationToken);
    }
}