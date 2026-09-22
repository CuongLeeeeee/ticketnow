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

    private const string ReleaseScript = """
        for i = 1, #KEYS / 2 do
          local qty = tonumber(ARGV[i])
          redis.call('INCRBY', KEYS[2*i-1], qty)
          local left = redis.call('DECRBY', KEYS[2*i], qty)
          if left <= 0 then redis.call('DEL', KEYS[2*i]) end
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

    public async Task ReleaseAsync(Guid userId, IEnumerable<(Guid TicketTypeId, int Quantity)> lines)
    {
        var list = lines.ToList();
        var keys = new RedisKey[list.Count * 2];
        var args = new RedisValue[list.Count];
        for (var i = 0; i < list.Count; i++)
        {
            keys[2 * i] = AvailableKey(list[i].TicketTypeId);
            keys[2 * i + 1] = UserKey(list[i].TicketTypeId, userId);
            args[i] = list[i].Quantity;
        }
        await _db.ScriptEvaluateAsync(ReleaseScript, keys, args);
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