using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace TicketNow.Notification.Api.Services;

public sealed class EmailSender(IConfiguration config, ILogger<EmailSender> log)
{
    public async Task SendAsync(string toEmail, string toName, string subject, string html, CancellationToken ct)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress("TicketNow", config["Email:From"] ?? "no-reply@ticketnow.local"));
        msg.To.Add(new MailboxAddress(toName, toEmail));
        msg.Subject = subject;
        msg.Body = new BodyBuilder { HtmlBody = html }.ToMessageBody();

        using var client = new SmtpClient();
        await client.ConnectAsync(config["Email:Host"] ?? "localhost",
            config.GetValue("Email:Port", 1025), SecureSocketOptions.None, ct);
        await client.SendAsync(msg, ct);
        await client.DisconnectAsync(true, ct);

        log.LogInformation("Sent email '{Subject}' to {Email}", subject, toEmail);
    }
}