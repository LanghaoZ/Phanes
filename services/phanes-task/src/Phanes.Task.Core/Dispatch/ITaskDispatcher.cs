namespace Phanes.Task.Core.Dispatch;

using System.Threading.Tasks;

/// <summary>Publishes one encoded TaskEvent, keyed by task_id. The only Kafka-producer seam.</summary>
public interface ITaskEventPublisher
{
    Task PublishAsync(TaskEvent evt, CancellationToken ct);
}

/// <summary>
/// The single outbound door for events: HTTP submission and actor follow-ups
/// both go through here, so kill, delay and lineage rules live in one place.
/// </summary>
public interface ITaskDispatcher
{
    Task SubmitAsync(TaskEvent evt, CancellationToken ct);

    /// <summary>
    /// In-process delayed publish. Bounded by DispatcherOptions.MaxDelaySeconds
    /// (throws DelayTooLongException beyond it). Pending timers are lost on
    /// restart — callers that need durable delays must not use this.
    /// </summary>
    Task SubmitWithDelayAsync(TaskEvent evt, TimeSpan delay, CancellationToken ct);

    /// <summary>Publish a KILL control event addressed to (taskType, taskId).</summary>
    Task KillAsync(string taskType, string taskId, CancellationToken ct);
}

public sealed class DelayTooLongException(TimeSpan delay, TimeSpan max)
    : Exception($"Delay {delay} exceeds the dispatcher maximum of {max}.");

public sealed class DispatcherClosedException()
    : Exception("The dispatcher is shutting down; the event was not accepted.");
