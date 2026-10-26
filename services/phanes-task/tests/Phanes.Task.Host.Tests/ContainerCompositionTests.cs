using Autofac;
using Autofac.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phanes.Task.Core.Actor;
using Phanes.Task.Core.Options;
using Phanes.Task.Core.Registry;
using Phanes.Task.Core.Status;
using Phanes.Task.Sdk;
using Phanes.Task.Tasks;

namespace HostTests;

public class ContainerCompositionTests
{
    private static IContainer BuildContainer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<TaskOptions>().Configure(o => { });
        var builder = new ContainerBuilder();
        builder.Populate(services);
        builder.RegisterModule<Phanes.Task.Host.TaskModule>();
        return builder.Build();
    }

    [Fact]
    public void Every_built_in_task_type_is_registered_and_resolvable_per_actor_scope()
    {
        using var container = BuildContainer();
        var types = container.Resolve<ITaskTypeRegistry>();
        var factory = container.Resolve<IActorScopeFactory>();

        Assert.Equal(
            new[] { PhanesNativeTaskTypes.Countdown, PhanesNativeTaskTypes.Echo, PhanesNativeTaskTypes.Flaky, PhanesNativeTaskTypes.Slow },
            types.All.Select(t => t.TaskType).OrderBy(x => x));

        using var a = factory.Create(PhanesNativeTaskTypes.Echo, "t1");
        using var b = factory.Create(PhanesNativeTaskTypes.Echo, "t2");
        Assert.IsType<EchoTask>(a.Task);
        Assert.NotSame(a.Task, b.Task);                   // one instance per actor scope
        using var exec = a.BeginExecution();
        Assert.NotNull(exec.Services.GetService(typeof(ITaskTypeRegistry))); // singletons visible from execution scope
    }

    [Fact]
    public void Unknown_task_type_is_a_KeyNotFound_at_the_factory()
    {
        using var container = BuildContainer();
        var factory = container.Resolve<IActorScopeFactory>();
        Assert.Throws<KeyNotFoundException>(() => factory.Create("nope.nope", "t1"));
    }

    [Fact]
    public void Empty_redis_connection_string_falls_back_to_the_in_memory_store()
    {
        using var container = BuildContainer();
        Assert.IsType<InMemoryExecutionStatusStore>(container.Resolve<IExecutionStatusStore>());
    }
}
