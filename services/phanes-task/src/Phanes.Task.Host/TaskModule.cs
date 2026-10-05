using System.Reflection;
using Autofac;
using Phanes.Task.Core.Actor;
using Phanes.Task.Core.Registry;
using Phanes.Task.Tasks;

namespace Phanes.Task.Host;

/// <summary>
/// Autofac module for phanes-task framework internals. Wires up the task
/// registry (assembly-scanned at startup), actor registry, and supporting
/// services. TaskLoop itself is registered as a HostedService in Program.cs.
/// </summary>
internal sealed class TaskModule : Autofac.Module
{
    protected override void Load(ContainerBuilder builder)
    {
        // Built-in tasks live in Phanes.Task.Tasks. Phase 4 will add plugin
        // assemblies loaded from disk via a separate registration path.
        var taskAssemblies = new[] { typeof(DummyTask).Assembly };

        builder.Register(ctx => new TaskRegistry(
                taskAssemblies,
                ctx.Resolve<Microsoft.Extensions.Logging.ILoggerFactory>()))
            .As<ITaskRegistry>()
            .SingleInstance();

        builder.RegisterType<ActorRegistry>()
            .As<IActorRegistry>()
            .SingleInstance();
    }
}
