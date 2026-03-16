using Autofac;

namespace Phanes.Task.Host;

/// <summary>
/// Autofac module for phanes-task framework internals.
/// Register TaskLoop, ActorRegistry, WatermarkManager, TaskFactory, etc. here.
/// </summary>
internal sealed class TaskModule : Autofac.Module
{
    protected override void Load(ContainerBuilder builder)
    {
        // TODO: register ITaskActor, IActorRegistry, IWatermarkManager, TaskLoop
    }
}
