using Autofac;
using Autofac.Core.Registration;
using Autofac.Extensions.DependencyInjection;
using Phanes.Task.Core.Actor;
using Phanes.Task.Sdk;

namespace Phanes.Task.Host;

/// <summary>
/// IActorScopeFactory on Autofac. Lifetimes:
///   root ──► "actor" scope (one per actor; holds the ITask instance)
///                └──► "execution" scope (one per event)
/// Task types are keyed registrations: Keyed&lt;ITask&gt;(taskType),
/// InstancePerMatchingLifetimeScope("actor").
/// </summary>
internal sealed class AutofacActorScopeFactory(ILifetimeScope root) : IActorScopeFactory
{
    public const string ActorTag = "actor";
    public const string ExecutionTag = "execution";

    public IActorScope Create(string taskType, string taskId)
    {
        var scope = root.BeginLifetimeScope(ActorTag);
        try
        {
            var task = scope.ResolveKeyed<ITask>(taskType);
            return new ActorScope(scope, task);
        }
        catch (ComponentNotRegisteredException ex)
        {
            scope.Dispose();
            throw new KeyNotFoundException($"No ITask registered for task_type '{taskType}' (task_id '{taskId}').", ex);
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    private sealed class ActorScope(ILifetimeScope scope, ITask task) : IActorScope
    {
        public ITask Task => task;

        public IExecutionScope BeginExecution() => new ExecutionScope(scope.BeginLifetimeScope(ExecutionTag));

        public void Dispose() => scope.Dispose();
    }

    private sealed class ExecutionScope(ILifetimeScope scope) : IExecutionScope
    {
        public IServiceProvider Services { get; } = new AutofacServiceProvider(scope);

        public void Dispose() => scope.Dispose();
    }
}
