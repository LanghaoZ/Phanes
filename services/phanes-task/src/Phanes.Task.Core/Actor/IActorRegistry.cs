namespace Phanes.Task.Core.Actor;

/// <summary>
/// In-memory registry of actors, keyed by (task_type, task_id).
///
/// THREADING: confined to the Task Loop thread. Every member is called from
/// that single thread, so the implementation holds no locks. Do not call it
/// from request handlers or other threads; expose a snapshot through the loop
/// if a management API ever needs this data.
/// </summary>
public interface IActorRegistry
{
    /// <summary>
    /// Return the live actor for (taskType, taskId), creating and starting one
    /// if none exists or the existing one has retired.
    /// Throws KeyNotFoundException if taskType has no registered ITask.
    /// </summary>
    ITaskActor GetOrCreateActor(string taskId, string taskType);

    /// <summary>
    /// Remove retired actors. Never stops a live actor — actors retire
    /// themselves. Returns the number of entries removed.
    /// </summary>
    int CleanupCompletedActors();

    /// <summary>Count of live actors for a task type.</summary>
    int RunningCount(string taskType);

    /// <summary>
    /// Process shutdown: signal every actor to stop and wait (bounded) for
    /// their run loops to exit.
    /// </summary>
    global::System.Threading.Tasks.Task CleanupAsync();
}
