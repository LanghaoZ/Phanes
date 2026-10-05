namespace Phanes.Task.Core.Actor;

/// <summary>
/// In-memory registry of active actors, keyed by task_id.
/// Actors are created on first event arrival and removed when completed or idle.
/// </summary>
public interface IActorRegistry
{
    /// <summary>
    /// Return the existing actor for taskId, or create and start a new one.
    /// Throws if the taskType has no registered ITask implementation.
    /// </summary>
    ITaskActor GetOrCreateActor(string taskId, string taskType);

    /// <summary>
    /// Remove completed and idle actors from the registry.
    /// Called periodically by the Task Loop cleanup scan.
    /// Returns the number of actors removed.
    /// </summary>
    int CleanupCompletedActors();

    /// <summary>
    /// Count of currently running actors for a given task type.
    /// </summary>
    int RunningCount(string taskType);

    /// <summary>
    /// Graceful shutdown: stop all running actors and drain their channels.
    /// </summary>
    global::System.Threading.Tasks.Task CleanupAsync();
}
