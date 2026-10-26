using Microsoft.Extensions.Options;
using Phanes.Task.Core.Options;
using StackExchange.Redis;

namespace Phanes.Task.Core.Status;

using System.Threading.Tasks;

/// <summary>
/// Redis-backed status store.
///   {prefix}:{type}:{id}:ev:{eventId}  HASH rank/status/error/reason/updated_at
///   {prefix}:{type}:{id}:events        SET of event ids (for listing)
///   {prefix}:{type}:{id}:killed        STRING marker
/// The CAS runs as a Lua script so rank check + write are atomic.
/// </summary>
public sealed class RedisExecutionStatusStore : IExecutionStatusStore
{
    // KEYS[1]=event hash, KEYS[2]=events set; ARGV: rank, status, error, reason, updated_at, ttl, event_id
    // Returns 1 applied, 0 stale, 2 already terminal.
    private const string CasScript = """
        local cur = redis.call('HGET', KEYS[1], 'rank')
        if cur then
          if tonumber(cur) >= 10 then return 2 end
          if tonumber(cur) >= tonumber(ARGV[1]) then return 0 end
        end
        redis.call('HSET', KEYS[1], 'rank', ARGV[1], 'status', ARGV[2], 'error', ARGV[3], 'reason', ARGV[4], 'updated_at', ARGV[5])
        redis.call('EXPIRE', KEYS[1], ARGV[6])
        redis.call('SADD', KEYS[2], ARGV[7])
        redis.call('EXPIRE', KEYS[2], ARGV[6])
        return 1
        """;

    private readonly IDatabase _db;
    private readonly string _prefix;
    private readonly int _ttl;

    public RedisExecutionStatusStore(IConnectionMultiplexer redis, IOptions<TaskOptions> options)
    {
        _db = redis.GetDatabase();
        _prefix = options.Value.Redis.KeyPrefix;
        _ttl = options.Value.Redis.StatusTtlSeconds;
    }

    private string EventKey(string t, string id, string ev) => $"{_prefix}:{t}:{id}:ev:{ev}";
    private string EventsKey(string t, string id) => $"{_prefix}:{t}:{id}:events";
    private string KilledKey(string t, string id) => $"{_prefix}:{t}:{id}:killed";

    public async Task<StatusWriteResult> TrySetAsync(
        string taskType, string taskId, string eventId, ExecutionStatus status,
        string? error = null, string? completionReason = null, CancellationToken ct = default)
    {
        var result = (int)await _db.ScriptEvaluateAsync(
            CasScript,
            new RedisKey[] { EventKey(taskType, taskId, eventId), EventsKey(taskType, taskId) },
            new RedisValue[]
            {
                status.Rank(), status.Wire(), error ?? string.Empty, completionReason ?? string.Empty,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _ttl, eventId,
            });
        return result switch
        {
            1 => StatusWriteResult.Applied,
            2 => StatusWriteResult.AlreadyTerminal,
            _ => StatusWriteResult.Stale,
        };
    }

    public async Task<bool> IsCompletedAsync(string taskType, string taskId, string eventId, CancellationToken ct = default)
    {
        var rank = await _db.HashGetAsync(EventKey(taskType, taskId, eventId), "rank");
        return rank.HasValue && (int)rank >= 10;
    }

    public Task MarkKilledAsync(string taskType, string taskId, CancellationToken ct = default)
        => _db.StringSetAsync(KilledKey(taskType, taskId), "1", TimeSpan.FromSeconds(_ttl));

    public Task<bool> IsKilledAsync(string taskType, string taskId, CancellationToken ct = default)
        => _db.KeyExistsAsync(KilledKey(taskType, taskId));

    public async Task<IReadOnlyList<EventStatusRecord>> GetEventsAsync(string taskType, string taskId, CancellationToken ct = default)
    {
        var ids = await _db.SetMembersAsync(EventsKey(taskType, taskId));
        var records = new List<EventStatusRecord>(ids.Length);
        foreach (var id in ids)
        {
            var hash = await _db.HashGetAllAsync(EventKey(taskType, taskId, id!));
            if (hash.Length == 0)
            {
                continue;
            }
            var map = hash.ToDictionary(h => (string)h.Name!, h => (string?)h.Value);
            records.Add(new EventStatusRecord(
                id!,
                ExecutionStatusExtensions.Parse(map.GetValueOrDefault("status")),
                NullIfEmpty(map.GetValueOrDefault("error")),
                NullIfEmpty(map.GetValueOrDefault("reason")),
                DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(map.GetValueOrDefault("updated_at") ?? "0"))));
        }
        return records.OrderBy(r => r.UpdatedAt).ToList();
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
