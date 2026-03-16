namespace Phanes.Task.Sdk;

/// <summary>
/// Pluggable task handler interface. Implement this to define a task type.
/// External plugin assemblies reference Phanes.Task.Sdk only and implement this interface.
/// Handlers are discovered by convention scanning — no explicit registration required.
/// </summary>
public interface ITaskHandler
{
    TaskType TaskType { get; }

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
