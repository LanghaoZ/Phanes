using Phanes.Task.Core.Model;

namespace Phanes.Task.Core.Actor;

/// <summary>
/// Represents a running actor managing the lifecycle of one task instance.
/// One actor exists per (task_type, task_id). The actor owns a bounded mailbox
/// and drains it sequentially, delegating each event to its ITask.
///
/// Lifecycle is self-managed: the actor retires itself when its task reports
/// finished or when it has been idle past the configured timeout. Nothing
/// outside the actor stops a live actor except process shutdown. Retirement is
/// atomic with respect to EnqueueAsync, so an event is either accepted by a
/// live actor or rejected with NotRunning — never dropped into a dead mailbox.
/// </summary>
public interface ITaskActor
{
    string TaskId   { get; }
    string TaskType { get; }

    /// <summary>True until the actor retires. A retired actor never restarts.</summary>
    bool IsRunning { get; }

    /// <summary>True once the actor has retired and its run loop is exiting.</summary>
    bool IsCompleted { get; }

    /// <summary>
    /// Deliver an event. Waits while the mailbox is full (backpressure — the
    /// caller's partition pauses; events are never dropped). Returns NotRunning
    /// if the actor retired before the event could be accepted.
    /// </summary>
    ValueTask<EnqueueResult> EnqueueAsync(TaskEventEnvelope envelope, CancellationToken ct);

    /// <summary>
    /// The actor's processing loop. Completes when the actor retires or the
    /// token is cancelled (process shutdown).
    /// </summary>
    global::System.Threading.Tasks.Task RunAsync(CancellationToken ct);

    // Phase 3 — actor state persistence
    global::System.Threading.Tasks.Task SnapshotAsync();
    global::System.Threading.Tasks.Task RestoreAsync(string taskId);
}
