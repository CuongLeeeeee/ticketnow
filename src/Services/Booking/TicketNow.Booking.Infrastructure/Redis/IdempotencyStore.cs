using System.Text.Json;
using StackExchange.Redis;

namespace TicketNow.Booking.Infrastructure.Redis;

public enum IdempotencyState { Started, InProgress, Completed }

public sealed record StoredResponse(int StatusCode, string Body);

public sealed class IdempotencyStore(IConnectionMultiplexer redis)
{
    private const string Processing = "__processing__";
    private readonly IDatabase _db = redis.GetDatabase();

    private static RedisKey Key(Guid userId, string key) => $"idem:{userId}:{key}";

    public async Task<(IdempotencyState State, StoredResponse? Response)> TryBeginAsync(Guid userId, string key)
    {
        // Set an "in progress" flag with a 30s TTL so it expires on its own if the service dies mid-request
        if (await _db.StringSetAsync(Key(userId, key), Processing, TimeSpan.FromSeconds(30), When.NotExists))
            return (IdempotencyState.Started, null);

        var existing = await _db.StringGetAsync(Key(userId, key));
        if (existing.IsNull || existing == Processing)
            return (IdempotencyState.InProgress, null);

        return (IdempotencyState.Completed, JsonSerializer.Deserialize<StoredResponse>(existing.ToString()));
    }

    public Task CompleteAsync(Guid userId, string key, StoredResponse response) =>
        _db.StringSetAsync(Key(userId, key), JsonSerializer.Serialize(response), TimeSpan.FromHours(24));

    public Task AbortAsync(Guid userId, string key) =>
        _db.KeyDeleteAsync(Key(userId, key));
}