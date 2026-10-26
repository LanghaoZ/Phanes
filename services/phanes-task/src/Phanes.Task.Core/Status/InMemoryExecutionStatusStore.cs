using System.Collections.Concurrent;

namespace Phanes.Task.Core.Status;

using System.Threading.Tasks;

/// <summary>Process-local store for dev and tests. Same semantics as the Redis store.</summary>
public sealed class InMemoryExecutionStatusStore : IExecutionStatusStore
{
    private readonly ConcurrentDictionary<string, EventStatusRecord> _events = new();
    private readonly ConcurrentDictionary<string, byte> _killed = new();
    private readonly object _gate = new();

    private static string Key(string t, string id, string ev) => $"{t}|{id}|{ev}";
    private static string TaskKey(string t, string id) => $"{t}|{id}";

    public Task<StatusWriteResult> TrySetAsync(
        string taskType, string taskId, string eventId, ExecutionStatus status,
        string? error = null, string? completionReason = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var key = Key(taskType, taskId, eventId);
            if (_events.TryGetValue(key, out var current))
            {
                if (current.Status.IsTerminal())
                {
                    return Task.FromResult(StatusWriteResult.AlreadyTerminal);
                }
                if (current.Status.Rank() >= status.Rank())
                {
                    return Task.FromResult(StatusWriteResult.Stale);
                }
            }
            _events[key] = new EventStatusRecord(eventId, status, error, completionReason, DateTimeOffset.UtcNow);
            return Task.FromResult(StatusWriteResult.Applied);
        }
    }

    public Task<bool> IsCompletedAsync(string taskType, string taskId, string eventId, CancellationToken ct = default)
        => Task.FromResult(
            _events.TryGetValue(Key(taskType, taskId, eventId), out var r) && r.Status.IsTerminal());

    public Task MarkKilledAsync(string taskType, string taskId, CancellationToken ct = default)
    {
        _killed[TaskKey(taskType, taskId)] = 1;
        return Task.CompletedTask;
    }

    public Task<bool> IsKilledAsync(string taskType, string taskId, CancellationToken ct = default)
        => Task.FromResult(_killed.ContainsKey(TaskKey(taskType, taskId)));

    public Task<IReadOnlyList<EventStatusRecord>> GetEventsAsync(string taskType, string taskId, CancellationToken ct = default)
    {
        var prefix = TaskKey(taskType, taskId) + "|";
        IReadOnlyList<EventStatusRecord> list = _events
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(kv => kv.Value)
            .OrderBy(r => r.UpdatedAt)
            .ToList();
        return Task.FromResult(list);
    }
}
