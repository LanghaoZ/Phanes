namespace Phanes.Task.Core.Status;

using System.Threading.Tasks;

public enum StatusWriteResult
{
    Applied,
    /// <summary>A same-or-higher non-terminal rank is already recorded (duplicate delivery).</summary>
    Stale,
    /// <summary>The event already has a terminal status.</summary>
    AlreadyTerminal,
}

public sealed record EventStatusRecord(
    string EventId,
    ExecutionStatus Status,
    string? Error,
    string? CompletionReason,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Durable per-event execution status plus a per-task kill marker. Keyed by
/// (task_type, task_id, event_id). This is load-bearing for idempotency —
/// the loop and actor consult it to skip replays and duplicate deliveries —
/// not just a query surface.
/// </summary>
public interface IExecutionStatusStore
{
    /// <summary>Monotonic CAS write; see ExecutionStatus ranks.</summary>
    Task<StatusWriteResult> TrySetAsync(
        string taskType, string taskId, string eventId, ExecutionStatus status,
        string? error = null, string? completionReason = null, CancellationToken ct = default);

    Task<bool> IsCompletedAsync(string taskType, string taskId, string eventId, CancellationToken ct = default);

    Task MarkKilledAsync(string taskType, string taskId, CancellationToken ct = default);
    Task<bool> IsKilledAsync(string taskType, string taskId, CancellationToken ct = default);

    Task<IReadOnlyList<EventStatusRecord>> GetEventsAsync(string taskType, string taskId, CancellationToken ct = default);
}
