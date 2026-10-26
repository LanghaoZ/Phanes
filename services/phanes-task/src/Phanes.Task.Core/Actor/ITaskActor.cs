using Phanes.Task.Core.Model;

namespace Phanes.Task.Core.Actor;

using System.Threading.Tasks;

/// <summary>
/// Represents a running actor managing the lifecycle of one task instance.
/// One actor exists per (task_type, task_id). The actor owns a bounded mailbox
/// and drains it sequentially, delegating each event to its ITask.
///
/// Lifecycle is self-managed: the actor retires itself when its task reports
/// finished, when it has been idle past the configured timeout, or when
/// killed. Retirement is atomic with respect to EnqueueAsync, so an event is
/// either accepted by a live actor or rejected with NotRunning — never dropped
/// into a dead mailbox.
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
    /// Kill: cancel the in-flight execution, record Killed for it and for any
    /// buffered events, release their offsets, and retire.
    /// </summary>
    void RequestKill();

    /// <summary>
    /// The actor's processing loop. Completes when the actor retires or the
    /// token is cancelled (process shutdown).
    /// </summary>
    Task RunAsync(CancellationToken ct);

    // Phase 3 — actor state persistence
    Task SnapshotAsync();
    Task RestoreAsync(string taskId);
}
