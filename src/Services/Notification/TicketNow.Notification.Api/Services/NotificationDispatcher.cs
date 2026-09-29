using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;
using TicketNow.Notification.Api.Hubs;

namespace TicketNow.Notification.Api.Services;

public sealed record NotificationPayload(
    string Kind, Guid OrderId, string Title, string Message, DateTimeOffset CreatedAt);

public sealed class NotificationDispatcher(
    IConnectionMultiplexer redis,
    UserDirectory directory,
    EmailSender email,
    IHubContext<NotificationHub> hub,
    ILogger<NotificationDispatcher> log)
{
    private readonly IDatabase _db = redis.GetDatabase();

    public async Task SendAsync(
        Guid messageId, Guid userId, NotificationPayload payload,
        string emailSubject, string emailHtml, CancellationToken ct)
    {
        // Prevent duplicate sending at the Redis level: a redelivered message still targets a single email.
        var first = await _db.StringSetAsync(
            $"notif:sent:{messageId}", "1", TimeSpan.FromDays(7), When.NotExists);

        if (!first)
        {
            log.LogInformation("Notification {MessageId} was already sent, skipping", messageId);
            return;
        }

        // History: keeps the 50 most recent notifications, automatically expires after 30 days
        var listKey = $"notif:inbox:{userId}";
        await _db.ListLeftPushAsync(listKey, JsonSerializer.Serialize(payload));
        await _db.ListTrimAsync(listKey, 0, 49);
        await _db.KeyExpireAsync(listKey, TimeSpan.FromDays(30));

        await hub.Clients.User(userId.ToString()).SendAsync("notification", payload, ct);

        var contact = await directory.GetAsync(userId);
        if (contact is null)
        {
            log.LogWarning("No email found for user {UserId}, skipping email delivery", userId);
            return;
        }

        await email.SendAsync(contact.Email, contact.FullName, emailSubject, emailHtml, ct);
    }
}