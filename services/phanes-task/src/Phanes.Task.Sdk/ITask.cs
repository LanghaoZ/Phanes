using Phanes.Task.Contracts;

namespace Phanes.Task.Sdk;

/// <summary>
/// Pluggable task interface. Implement this to define a task type.
/// External plugin assemblies reference Phanes.Task.Sdk only and implement this interface.
/// Implementations are discovered by convention scanning — no explicit registration required.
///
/// Naming convention for TaskType: "&lt;owner&gt;.&lt;name&gt;".
/// First-party tasks use the "phanes." prefix (see PhanesNativeTaskTypes).
/// Plugin tasks use their service's prefix, e.g. "openinsights.data_ingestion".
/// </summary>
public interface ITask
{
    string TaskType { get; }

    /// <summary>
    /// Called once per task type at startup, before any execution begins.
    /// Register all shared resources (connection pools, clients, caches) into ctx here.
    /// Framework pre-populates ctx with well-known infrastructure before this call.
    /// </summary>
    void Configure(ITaskContext ctx);

    /// <summary>
    /// Process one TaskEvent. Called by the actor for each event in sequence.
    /// Receives the type-scoped context populated during Configure.
    /// </summary>
    Task<TaskResult> ExecuteAsync(TaskEvent evt, ITaskContext ctx, CancellationToken ct = default);

    /// <summary>
    /// Called by the actor after ExecuteAsync returns to determine whether this
    /// task has reached a terminal state and the actor can be retired.
    /// </summary>
    bool IsFinished();
}
