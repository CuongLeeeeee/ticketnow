using StackExchange.Redis;

namespace TicketNow.Booking.Infrastructure.Redis;

public enum WaitingRoomState { Disabled, NotInQueue, Waiting, Admitted }

public sealed record WaitingRoomStatus(WaitingRoomState State, long Position, long QueueLength);

public sealed record WaitingRoomConfig(bool Enabled, int AdmitPerTick, int AdmitTtlSeconds);

public sealed class WaitingRoomStore(IConnectionMultiplexer redis)
{
    private readonly IDatabase _db = redis.GetDatabase();

    // Hash tag {eventId}: all keys of one event land in the same slot,
    // so the Lua scripts keep working if we move to Redis Cluster
    private static string Tag(Guid eventId) => $"wr:{{{eventId}}}";
    private static RedisKey QueueKey(Guid e) => $"{Tag(e)}:queue";
    private static RedisKey ConfigKey(Guid e) => $"{Tag(e)}:cfg";
    private static RedisKey AdmitKey(Guid e, Guid u) => $"{Tag(e)}:admit:{u}";
    private static string AdmitPrefix(Guid e) => $"{Tag(e)}:admit:";
    private static readonly RedisKey EventsKey = "wr:events";

    private const string JoinScript = """
        if redis.call('EXISTS', KEYS[2]) == 1 then return {2, 0, 0} end
        redis.call('ZADD', KEYS[1], 'NX', ARGV[1], ARGV[2])
        local rank = redis.call('ZRANK', KEYS[1], ARGV[2])
        return {1, rank + 1, redis.call('ZCARD', KEYS[1])}
        """;

    private const string StatusScript = """
        if redis.call('EXISTS', KEYS[2]) == 1 then return {2, 0, 0} end
        local rank = redis.call('ZRANK', KEYS[1], ARGV[1])
        if not rank then return {0, 0, 0} end
        return {1, rank + 1, redis.call('ZCARD', KEYS[1])}
        """;

    // Pop the first N users from the queue and grant them admission, atomically
    private const string AdmitScript = """
        local popped = redis.call('ZPOPMIN', KEYS[1], tonumber(ARGV[1]))
        local n = 0
        for i = 1, #popped, 2 do
          redis.call('SET', ARGV[2] .. popped[i], '1', 'EX', ARGV[3])
          n = n + 1
        end
        return n
        """;

    public async Task EnableAsync(Guid eventId, int admitPerTick, int admitTtlSeconds)
    {
        await _db.HashSetAsync(ConfigKey(eventId),
        [
            new HashEntry("enabled", 1),
            new HashEntry("admitPerTick", admitPerTick),
            new HashEntry("admitTtlSeconds", admitTtlSeconds)
        ]);
        await _db.SetAddAsync(EventsKey, eventId.ToString());
    }

    public async Task DisableAsync(Guid eventId)
    {
        await _db.HashSetAsync(ConfigKey(eventId), "enabled", 0);
        await _db.SetRemoveAsync(EventsKey, eventId.ToString());
    }

    public async Task<WaitingRoomConfig?> GetConfigAsync(Guid eventId)
    {
        var h = await _db.HashGetAllAsync(ConfigKey(eventId));
        if (h.Length == 0) return null;
        var map = h.ToDictionary(x => x.Name.ToString(), x => x.Value);
        return new WaitingRoomConfig(
            (int)map["enabled"] == 1,
            (int)map["admitPerTick"],
            (int)map["admitTtlSeconds"]);
    }

    public async Task<Guid[]> GetEnabledEventsAsync() =>
        (await _db.SetMembersAsync(EventsKey)).Select(v => Guid.Parse(v.ToString())).ToArray();

    public Task<WaitingRoomStatus> JoinAsync(Guid eventId, Guid userId) =>
        RunAsync(JoinScript, eventId, userId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), userId.ToString());

    public Task<WaitingRoomStatus> StatusAsync(Guid eventId, Guid userId) =>
        RunAsync(StatusScript, eventId, userId, userId.ToString());

    public async Task<int> AdmitAsync(Guid eventId, int count, int ttlSeconds)
    {
        var result = await _db.ScriptEvaluateAsync(AdmitScript,
            [QueueKey(eventId)],
            [count, AdmitPrefix(eventId), ttlSeconds]);
        return (int)result;
    }

    public Task<bool> IsAdmittedAsync(Guid eventId, Guid userId) =>
        _db.KeyExistsAsync(AdmitKey(eventId, userId));

    private async Task<WaitingRoomStatus> RunAsync(string script, Guid eventId, Guid userId, params RedisValue[] args)
    {
        var raw = (RedisResult[])(await _db.ScriptEvaluateAsync(script,
            [QueueKey(eventId), AdmitKey(eventId, userId)], args))!;

        var state = (int)raw[0] switch
        {
            2 => WaitingRoomState.Admitted,
            1 => WaitingRoomState.Waiting,
            _ => WaitingRoomState.NotInQueue
        };
        return new WaitingRoomStatus(state, (long)raw[1], (long)raw[2]);
    }
}