using System.Text.Json;
using StackExchange.Redis;

namespace TicketNow.Notification.Api.Services;

public sealed record UserContact(string Email, string FullName);

public sealed class UserDirectory(IConnectionMultiplexer redis)
{
    private readonly IDatabase _db = redis.GetDatabase();
    private static RedisKey Key(Guid userId) => $"notif:user:{userId}";

    public Task SaveAsync(Guid userId, UserContact contact) =>
        _db.StringSetAsync(Key(userId), JsonSerializer.Serialize(contact));

    public async Task<UserContact?> GetAsync(Guid userId)
    {
        var v = await _db.StringGetAsync(Key(userId));
        return v.IsNull ? null : JsonSerializer.Deserialize<UserContact>(v.ToString());
    }
}