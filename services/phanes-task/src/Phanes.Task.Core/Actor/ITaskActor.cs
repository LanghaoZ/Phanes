using Phanes.Task.Sdk;

namespace Phanes.Task.Core.Actor;

/// <summary>
/// Represents a running actor managing the lifecycle of one task instance.
/// One actor exists per task_id. The actor owns a bounded Channel&lt;TaskEvent&gt;
/// and drains it sequentially, delegating each event to its ITask implementation.
/// </summary>
public interface ITaskActor
{
    string TaskId      { get; }
    bool   IsCompleted { get; }

    /// <summary>
    /// True when no events have arrived within the configured idle timeout.
    /// Idle actors are candidates for eviction by the periodic registry scan.
    /// </summary>
    bool IsIdle { get; }

    /// <summary>
    /// Enqueue a TaskEvent for processing. Returns false if the actor's channel
    /// is full (backpressure). The Task Loop logs a warning on false.
    /// </summary>
    bool TryEnqueue(TaskEvent evt);

    /// <summary>
    /// Start the actor's event processing loop. Runs until the channel is closed
    /// or the cancellation token is triggered.
    /// </summary>
    global::System.Threading.Tasks.Task RunAsync(CancellationToken ct);

    // Phase 3 — actor state persistence
    global::System.Threading.Tasks.Task SnapshotAsync();
    global::System.Threading.Tasks.Task RestoreAsync(string taskId);
}
