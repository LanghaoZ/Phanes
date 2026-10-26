using Phanes.Task.Sdk;

namespace Phanes.Task.Core.Actor;

using System.Threading.Tasks;

/// <summary>Services scoped to one event execution (disposed after ExecuteAsync).</summary>
public interface IExecutionScope : IDisposable
{
    IServiceProvider Services { get; }
}

/// <summary>
/// The DI lifetime scope behind one actor. Owns the task instance (one per
/// task_id); opens a child scope per execution. Disposed when the actor retires.
/// </summary>
public interface IActorScope : IDisposable
{
    ITask Task { get; }
    IExecutionScope BeginExecution();
}

/// <summary>
/// Opens actor scopes. Implemented by the host on top of the DI container;
/// Core only sees these interfaces. Throws KeyNotFoundException when no task
/// is registered for the type.
/// </summary>
public interface IActorScopeFactory
{
    IActorScope Create(string taskType, string taskId);
}
