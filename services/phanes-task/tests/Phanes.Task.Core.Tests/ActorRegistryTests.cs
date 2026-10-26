using Microsoft.Extensions.Logging.Abstractions;
using Phanes.Task.Core.Actor;
using Phanes.Task.Core.Options;
using static CoreTests.TestUtil;

namespace CoreTests;

public class ActorRegistryTests
{
    private static ActorRegistry NewRegistry(params FakeTask[] tasks)
        => new(
            new FakeTaskRegistry(tasks),
            Microsoft.Extensions.Options.Options.Create(new TaskOptions()),
            NullLoggerFactory.Instance);

    [Fact]
    public async Task Returns_the_same_live_actor_for_the_same_task()
    {
        var registry = NewRegistry(new FakeTask());

        var first = registry.GetOrCreateActor("t1", "test.fake");
        var second = registry.GetOrCreateActor("t1", "test.fake");

        Assert.Same(first, second);
        await registry.CleanupAsync();
    }

    [Fact]
    public async Task Same_task_id_under_different_types_gets_separate_actors()
    {
        var registry = NewRegistry(new FakeTask("type.a"), new FakeTask("type.b"));

        var a = registry.GetOrCreateActor("t1", "type.a");
        var b = registry.GetOrCreateActor("t1", "type.b");

        Assert.NotSame(a, b);
        await registry.CleanupAsync();
    }

    [Fact]
    public async Task Replaces_a_retired_actor_with_a_fresh_one()
    {
        var task = new FakeTask { Finished = () => true };
        var registry = NewRegistry(task);

        var first = registry.GetOrCreateActor("t1", "test.fake");
        await first.EnqueueAsync(Envelope("t1", "e0"), default);
        await Eventually(() => first.IsCompleted, "first actor retired");

        var second = registry.GetOrCreateActor("t1", "test.fake");

        Assert.NotSame(first, second);
        Assert.True(second.IsRunning);
        await registry.CleanupAsync();
    }

    [Fact]
    public async Task Cleanup_scan_removes_retired_actors_and_leaves_live_ones_alone()
    {
        var finishing = new FakeTask("type.done") { Finished = () => true };
        var registry = NewRegistry(finishing, new FakeTask("type.live"));

        var done = registry.GetOrCreateActor("t1", "type.done");
        var live = registry.GetOrCreateActor("t2", "type.live");
        await done.EnqueueAsync(Envelope("t1", "e0"), default);
        await Eventually(() => done.IsCompleted, "actor retired");

        Assert.Equal(1, registry.CleanupCompletedActors());
        Assert.Equal(0, registry.CleanupCompletedActors());
        Assert.True(live.IsRunning);
        Assert.Same(live, registry.GetOrCreateActor("t2", "type.live"));
        await registry.CleanupAsync();
    }

    [Fact]
    public async Task Running_count_is_per_task_type_and_excludes_retired_actors()
    {
        var finishing = new FakeTask("type.done") { Finished = () => true };
        var registry = NewRegistry(finishing, new FakeTask("type.live"));

        registry.GetOrCreateActor("a", "type.live");
        registry.GetOrCreateActor("b", "type.live");
        var done = registry.GetOrCreateActor("c", "type.done");
        await done.EnqueueAsync(Envelope("c", "e0"), default);
        await Eventually(() => done.IsCompleted, "actor retired");

        Assert.Equal(2, registry.RunningCount("type.live"));
        Assert.Equal(0, registry.RunningCount("type.done"));
        Assert.Equal(0, registry.RunningCount("type.unknown"));
        await registry.CleanupAsync();
    }

    [Fact]
    public void Unknown_task_type_throws()
    {
        var registry = NewRegistry(new FakeTask());

        Assert.Throws<KeyNotFoundException>(() => registry.GetOrCreateActor("t1", "nope"));
    }

    [Fact]
    public async Task Shutdown_stops_every_actor()
    {
        var registry = NewRegistry(new FakeTask());
        var actor = registry.GetOrCreateActor("t1", "test.fake");

        await registry.CleanupAsync().WaitAsync(Patience);

        Assert.False(actor.IsRunning);
    }
}
