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

        // Overwriting is idempotent, so no dedupe is needed
        await directory.SaveAsync(m.UserId, new UserContact(m.Email, m.FullName));

        await email.SendAsync(m.Email, m.FullName, "Welcome to TicketNow",
            EmailTemplates.Layout("Your account is ready",
                $"<p>Hi <strong>{m.FullName}</strong>,</p><p>Your account has been created. Good luck getting your tickets!</p>"),
            context.CancellationToken);
    }
}