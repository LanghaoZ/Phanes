using Microsoft.Extensions.Logging.Abstractions;
using Phanes.Task.Core.Actor;
using Phanes.Task.Core.Dispatch;
using Phanes.Task.Core.Status;
using static CoreTests.TestUtil;

namespace CoreTests;

public class ActorRegistryTests
{
    private readonly ExecutionLog _log = new();
    private readonly FakeScopeFactory _scopes;
    private readonly ActorRegistry _registry;

    public ActorRegistryTests()
    {
        _scopes = new FakeScopeFactory(_log, "type.a", "type.b");
        _registry = new ActorRegistry(
            new FakeTypeRegistry("type.a", "type.b"),
            _scopes,
            new InMemoryExecutionStatusStore(),
            new TaskDispatcher(new FakePublisher(), Options(), NullLogger<TaskDispatcher>.Instance),
            Watermark(),
            Options(worker: FastWorker()),
            NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task Same_live_actor_for_the_same_task_separate_actors_per_type_and_per_id()
    {
        var a1 = _registry.GetOrCreateActor("t1", "type.a");
        Assert.Same(a1, _registry.GetOrCreateActor("t1", "type.a"));
        Assert.NotSame(a1, _registry.GetOrCreateActor("t1", "type.b"));
        Assert.NotSame(a1, _registry.GetOrCreateActor("t2", "type.a"));
        Assert.Same(a1, _registry.TryGetRunning("t1", "type.a"));
        Assert.Null(_registry.TryGetRunning("ghost", "type.a"));
        await _registry.CleanupAsync();
    }

    [Fact]
    public async Task Each_actor_gets_its_own_task_instance_in_its_own_scope()
    {
        _registry.GetOrCreateActor("t1", "type.a");
        _registry.GetOrCreateActor("t2", "type.a");

        var tasks = _scopes.Created.Select(s => s.FakeTask).ToArray();
        Assert.Equal(2, tasks.Length);
        Assert.NotSame(tasks[0], tasks[1]);
        Assert.Equal(["t1", "t2"], tasks.Select(t => t.TaskId).OrderBy(x => x));
        await _registry.CleanupAsync();
    }

    [Fact]
    public async Task Retired_actor_is_replaced_and_its_scope_disposed()
    {
        _log.Finished = _ => true;
        var first = _registry.GetOrCreateActor("t1", "type.a");
        await first.EnqueueAsync(Envelope("t1", "e0", taskType: "type.a"), default);
        await Eventually(() => first.IsCompleted, "first actor retired");

        var second = _registry.GetOrCreateActor("t1", "type.a");
        Assert.NotSame(first, second);
        Assert.True(second.IsRunning);
        Assert.True(_scopes.Created.Single(s => s.FakeTask.TaskId == "t1" && s.Disposed) is not null);
        await _registry.CleanupAsync();
    }

    [Fact]
    public async Task Cleanup_scan_removes_retired_only_and_running_count_is_per_type()
    {
        _log.Finished = t => t.TaskType == "type.b";
        var live1 = _registry.GetOrCreateActor("a", "type.a");
        _registry.GetOrCreateActor("b", "type.a");
        var done = _registry.GetOrCreateActor("c", "type.b");
        await done.EnqueueAsync(Envelope("c", "e0", taskType: "type.b"), default);
        await Eventually(() => done.IsCompleted, "actor retired");

        Assert.Equal(1, _registry.CleanupCompletedActors());
        Assert.Equal(0, _registry.CleanupCompletedActors());
        Assert.Equal(2, _registry.RunningCount("type.a"));
        Assert.Equal(0, _registry.RunningCount("type.b"));
        Assert.Same(live1, _registry.GetOrCreateActor("a", "type.a"));
        await _registry.CleanupAsync();
    }

    [Fact]
    public void Unknown_task_type_throws()
        => Assert.Throws<KeyNotFoundException>(() => _registry.GetOrCreateActor("t1", "nope"));

    [Fact]
    public async Task Shutdown_stops_every_actor()
    {
        var actor = _registry.GetOrCreateActor("t1", "type.a");
        await _registry.CleanupAsync().WaitAsync(Patience);
        Assert.False(actor.IsRunning);
    }
}
