using Autofac;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phanes.Task.Core.Actor;
using Phanes.Task.Core.Consumer;
using Phanes.Task.Core.Dispatch;
using Phanes.Task.Core.Options;
using Phanes.Task.Core.Registry;
using Phanes.Task.Core.Status;
using Phanes.Task.Core.Watermark;
using Phanes.Task.Sdk;
using Phanes.Task.Tasks;
using Serilog.Extensions.Logging;
using StackExchange.Redis;

namespace Phanes.Task.Host;

/// <summary>
/// Autofac module for phanes-task. Everything is registered ONCE here; the
/// lifetime declaration decides where instances live:
///   SingleInstance                        → root (loop, registry, watermark, status, dispatcher)
///   InstancePerMatchingLifetimeScope("actor") → one per actor (task instances)
/// TaskLoop itself is a HostedService (Program.cs).
/// </summary>
internal sealed class TaskModule : Autofac.Module
{
    protected override void Load(ContainerBuilder builder)
    {
        // Built-in tasks live in Phanes.Task.Tasks. Phase 4 adds plugin
        // assemblies through the same registry + keyed registration path.
        var taskAssemblies = new[] { typeof(EchoTask).Assembly };
        var registry = new TaskTypeRegistry(taskAssemblies, new SerilogLoggerFactory());
        builder.RegisterInstance(registry).As<ITaskTypeRegistry>().AsSelf();

        foreach (var descriptor in registry.All)
        {
            builder.RegisterType(descriptor.ClrType)
                .Keyed<ITask>(descriptor.TaskType)
                .InstancePerMatchingLifetimeScope(AutofacActorScopeFactory.ActorTag);
        }

        builder.RegisterType<AutofacActorScopeFactory>().As<IActorScopeFactory>().SingleInstance();
        builder.RegisterType<ActorRegistry>().As<IActorRegistry>().SingleInstance();
        builder.RegisterType<KafkaTaskEventSource>().As<ITaskEventSource>().SingleInstance();
        builder.RegisterType<WatermarkManager>().As<IWatermarkManager>().SingleInstance();
        builder.RegisterType<KafkaTaskEventPublisher>().As<ITaskEventPublisher>().SingleInstance();
        builder.RegisterType<TaskDispatcher>().As<ITaskDispatcher>().SingleInstance();

        builder.Register(ctx =>
            {
                var options = ctx.Resolve<IOptions<TaskOptions>>().Value.Redis;
                var logger = ctx.Resolve<ILogger<TaskModule>>();
                if (string.IsNullOrWhiteSpace(options.ConnectionString))
                {
                    logger.LogWarning("Redis:ConnectionString is empty — using the IN-MEMORY status store (not durable).");
                    return (IExecutionStatusStore)new InMemoryExecutionStatusStore();
                }
                var redis = ConnectionMultiplexer.Connect(options.ConnectionString);
                return new RedisExecutionStatusStore(redis, ctx.Resolve<IOptions<TaskOptions>>());
            })
            .As<IExecutionStatusStore>()
            .SingleInstance();
    }
}
