using Phanes.Task.Sdk;

namespace Phanes.Task.Core.Registry;

/// <summary>
/// Lookup table from task_type string to its ITask implementation and the
/// shared ITaskContext that the framework hands to ExecuteAsync.
///
/// Phase 1: populated at startup by scanning Phanes.Task.Tasks for ITask types.
/// Phase 4: plugin loading registers additional task types at runtime via the
/// same Register code path, just from a different AssemblyLoadContext.
/// </summary>
public interface ITaskRegistry
{
    /// <summary>
    /// Look up the registered task + context for a task_type string.
    /// Throws KeyNotFoundException if no implementation is registered.
    /// </summary>
    TaskRegistration Resolve(string taskType);

    /// <summary>
    /// Try-lookup variant — returns false if no implementation is registered.
    /// </summary>
    bool TryResolve(string taskType, out TaskRegistration registration);

    /// <summary>
    /// Returns the set of task_type strings currently registered.
    /// </summary>
    IReadOnlyCollection<string> RegisteredTaskTypes { get; }
}

/// <summary>
/// Bundle of a task implementation and its type-scoped context.
/// </summary>
public sealed record TaskRegistration(ITask Task, ITaskContext Context);
