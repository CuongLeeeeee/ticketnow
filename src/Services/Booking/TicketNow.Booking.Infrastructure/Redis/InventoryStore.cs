using StackExchange.Redis;

namespace TicketNow.Booking.Infrastructure.Redis;

public enum ReserveResult { Ok, SoldOut, LimitExceeded, NotInitialized }

public sealed class InventoryStore(IConnectionMultiplexer redis)
{
    private readonly IDatabase _db = redis.GetDatabase();

    private static RedisKey AvailableKey(Guid ticketTypeId) => $"inv:{ticketTypeId}";
    private static RedisKey UserKey(Guid ticketTypeId, Guid userId) => $"inv:user:{ticketTypeId}:{userId}";

    // KEYS[2i-1] = số vé còn lại, KEYS[2i] = số vé user này đang giữ
    // ARGV[2i-1] = số lượng muốn mua, ARGV[2i] = giới hạn mỗi người
    // Vòng 1 chỉ KIỂM TRA, vòng 2 mới TRỪ => tất cả hoặc không gì cả
    private const string ReserveScript = """
        local n = #KEYS / 2
        for i = 1, n do
          local available = tonumber(redis.call('GET', KEYS[2*i-1]) or '-1')
          if available < 0 then return {-3, i} end
          local qty = tonumber(ARGV[2*i-1])
          if available < qty then return {-1, i} end
          local held = tonumber(redis.call('GET', KEYS[2*i]) or '0')
          if held + qty > tonumber(ARGV[2*i]) then return {-2, i} end
        end
        for i = 1, n do
          local qty = tonumber(ARGV[2*i-1])
          redis.call('DECRBY', KEYS[2*i-1], qty)
          redis.call('INCRBY', KEYS[2*i], qty)
        end
        return {1, 0}
        """;

        // KEYS[1] = released:{orderId}   ARGV[1] = TTL (giây)
        // KEYS[2i] = inv:{tt}, KEYS[2i+1] = inv:user:{tt}:{user}   ARGV[i+1] = số lượng
        private const string ReleaseOnceScript = """
        if not redis.call('SET', KEYS[1], '1', 'NX', 'EX', ARGV[1]) then return 0 end
        local n = (#KEYS - 1) / 2
        for i = 1, n do
          local qty = tonumber(ARGV[i + 1])
          redis.call('INCRBY', KEYS[2*i], qty)
          local left = redis.call('DECRBY', KEYS[2*i + 1], qty)
          if left <= 0 then redis.call('DEL', KEYS[2*i + 1]) end
        end
        return 1
        """;

    public async Task<(ReserveResult Result, int FailedIndex)> ReserveAsync(
        Guid userId, IReadOnlyList<(Guid TicketTypeId, int Quantity, int MaxPerUser)> lines)
    {
        var keys = new RedisKey[lines.Count * 2];
        var args = new RedisValue[lines.Count * 2];
        for (var i = 0; i < lines.Count; i++)
        {
            keys[2 * i] = AvailableKey(lines[i].TicketTypeId);
            keys[2 * i + 1] = UserKey(lines[i].TicketTypeId, userId);
            args[2 * i] = lines[i].Quantity;
            args[2 * i + 1] = lines[i].MaxPerUser;
        }

        var raw = (RedisResult[])(await _db.ScriptEvaluateAsync(ReserveScript, keys, args))!;
        var code = (int)raw[0];
        var failedIndex = (int)raw[1] - 1;   // Lua đếm từ 1

        return code switch
        {
            1 => (ReserveResult.Ok, -1),
            -1 => (ReserveResult.SoldOut, failedIndex),
            -2 => (ReserveResult.LimitExceeded, failedIndex),
            _ => (ReserveResult.NotInitialized, failedIndex)
        };
    }

    public async Task<bool> ReleaseOnceAsync(Guid orderId, Guid userId, IEnumerable<(Guid TicketTypeId, int Quantity)> lines)
    {
        var list = lines.ToList();
        var keys = new RedisKey[1 + list.Count * 2];
        var args = new RedisValue[1 + list.Count];

        keys[0] = $"released:{orderId}";
        args[0] = (int)TimeSpan.FromDays(7).TotalSeconds;

        for (var i = 0; i < list.Count; i++)
        {
            keys[1 + 2 * i] = AvailableKey(list[i].TicketTypeId);
            keys[2 + 2 * i] = UserKey(list[i].TicketTypeId, userId);
            args[1 + i] = list[i].Quantity;
        }

        var result = await _db.ScriptEvaluateAsync(ReleaseOnceScript, keys, args);
        return (int)result == 1;
    }

    public Task<bool> InitAvailableAsync(Guid ticketTypeId, int available, bool overwrite) =>
        _db.StringSetAsync(AvailableKey(ticketTypeId), available,
            when: overwrite ? When.Always : When.NotExists);

    public Task<bool> InitUserHeldAsync(Guid ticketTypeId, Guid userId, int held, bool overwrite) =>
        _db.StringSetAsync(UserKey(ticketTypeId, userId), held,
            when: overwrite ? When.Always : When.NotExists);

    public async Task<long?> GetAvailableAsync(Guid ticketTypeId)
    {
        var v = await _db.StringGetAsync(AvailableKey(ticketTypeId));
        return v.IsNull ? null : (long)v;
    }
}