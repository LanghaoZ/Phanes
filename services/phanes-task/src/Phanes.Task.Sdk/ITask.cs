using Phanes.Task.Contracts;

namespace Phanes.Task.Sdk;

using System.Threading.Tasks;

/// <summary>
/// A task instance. The framework creates ONE instance per task_id (via the
/// DI container, inside that actor's lifetime scope) and feeds it that task's
/// events one at a time, in order. Instance fields are therefore per-task
/// state; dependencies arrive through the constructor.
///
/// Decorate implementations with <see cref="PhanesTaskAttribute"/>. For
/// type-level shared setup see <see cref="ITaskTypeSetup"/>.
/// </summary>
public interface ITask
{
    /// <summary>Process one event. Throwing marks the event failed; the actor keeps running.</summary>
    Task<TaskResult> ExecuteAsync(TaskExecution execution, TaskEvent evt, CancellationToken ct);

    /// <summary>
    /// Consulted after each event. True = this task instance has reached its
    /// terminal state and the actor may retire.
    /// </summary>
    bool IsFinished();
}

/// <summary>
/// Optional, implemented by a task class that needs type-level shared
/// resources (clients, caches). Called once per task type at startup on a
/// throwaway instance; register resources into ctx, then the context is
/// sealed and handed to every execution via <see cref="TaskExecution.Shared"/>.
/// Prefer constructor injection of singletons where possible.
/// </summary>
public interface ITaskTypeSetup
{
    void Configure(ITaskContext ctx);
}
