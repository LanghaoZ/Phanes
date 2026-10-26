using Microsoft.Extensions.Logging.Abstractions;
using Phanes.Task.Contracts;
using Phanes.Task.Core.Actor;
using Phanes.Task.Core.Consumer;
using Phanes.Task.Core.Dispatch;
using Phanes.Task.Core.Status;
using Phanes.Task.Core.Watermark;
using static CoreTests.TestUtil;

namespace CoreTests;

public class TaskLoopTests
{
    private const string Type = "test.fake";

    private sealed class Harness : IAsyncDisposable
    {
        public ExecutionLog Log { get; } = new();
        public FakeEventSource Source { get; } = new();
        public InMemoryExecutionStatusStore Status { get; } = new();
        public FakePublisher Publisher { get; } = new();
        public WatermarkManager Watermark { get; }
        public FakeScopeFactory Scopes { get; }
        public ActorRegistry Registry { get; }
        public TaskLoop Loop { get; }

        public Harness(IActorRegistry? registryOverride = null)
        {
            var options = Options(worker: FastWorker());
            Watermark = new WatermarkManager(options, NullLogger<WatermarkManager>.Instance);
            Scopes = new FakeScopeFactory(Log, Type);
            Registry = new ActorRegistry(new FakeTypeRegistry(Type), Scopes, Status,
                new TaskDispatcher(Publisher, options, NullLogger<TaskDispatcher>.Instance),
                Watermark, options, NullLoggerFactory.Instance);
            Loop = new TaskLoop(options, Source, registryOverride ?? Registry, Watermark, Status, NullLogger<TaskLoop>.Instance);
        }

        public Task StartAsync() => Loop.StartAsync(default);

        public long Position(int partition = 0) => Source.Positions.TryGetValue(partition, out var p) ? p : -1;

        public async ValueTask DisposeAsync()
        {
            await Loop.StopAsync(default).WaitAsync(Patience);
            Loop.Dispose();
        }
    }

    [Fact]
    public async Task Dispatches_events_and_stores_only_the_contiguous_completed_prefix()
    {
        await using var h = new Harness();
        await h.StartAsync();

        h.Source.Push(Event("t1", "e0"));
        h.Source.Push(Event("t2", "e1"));
        h.Source.Push(Event("t1", "e2"));

        await Eventually(() => h.Log.Executed.Count == 3, "all events executed");
        await Eventually(() => h.Position() == 3, "position stored past all three");
        Assert.All(new[] { ("t1", "e0"), ("t2", "e1"), ("t1", "e2") },
            async p => Assert.Equal(ExecutionStatus.Completed, await StatusOf(h.Status, Type, p.Item1, p.Item2)));
    }

    [Fact]
    public async Task Position_waits_for_a_slow_event_in_the_middle()
    {
        await using var h = new Harness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Log.OnExecute = async (_, evt, _) => { if (evt.EventId == "slow") await release.Task; return null; };
        await h.StartAsync();

        h.Source.Push(Event("fast1", "a"));
        h.Source.Push(Event("slow", "slow"));
        h.Source.Push(Event("fast2", "b"));

        await Eventually(() => h.Log.Executed.Count == 2, "fast events executed");
        await Task.Delay(50);
        Assert.Equal(1, h.Position()); // only offset 0 is committable

        release.SetResult();
        await Eventually(() => h.Position() == 3, "gap filled, position advanced");
    }

    [Fact]
    public async Task Skips_undecodable_incomplete_and_unknown_type_messages_without_stalling()
    {
        await using var h = new Harness();
        await h.StartAsync();

        h.Source.Push(new byte[] { 0x0A, 0x05, 0x01 });                        // truncated proto
        h.Source.Push((byte[]?)null);                                          // empty
        h.Source.Push(Event(taskId: "", eventId: "no-id"));                    // missing field
        h.Source.Push(Event("t1", "orphan", taskType: "not.registered"));      // unknown type
        h.Source.Push(Event("t1", "good"));

        await Eventually(() => h.Log.EventIds.Contains("good"), "valid event executed");
        await Eventually(() => h.Position() == 5, "every offset released");
        Assert.Equal(["good"], h.Log.EventIds.ToArray());
        Assert.Equal(ExecutionStatus.Failed, await StatusOf(h.Status, "not.registered", "t1", "orphan"));
    }

    [Fact]
    public async Task Replayed_terminal_event_is_skipped_and_received_is_recorded_for_new_ones()
    {
        await using var h = new Harness();
        await h.Status.TrySetAsync(Type, "t1", "done", ExecutionStatus.Completed);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Log.OnExecute = async (_, _, _) => { await gate.Task; return null; };
        await h.StartAsync();

        h.Source.Push(Event("t1", "done"));
        h.Source.Push(Event("t1", "fresh"));

        await Eventually(() => h.Status.GetEventsAsync(Type, "t1").Result.Any(e => e.EventId == "fresh"), "fresh event recorded");
        var fresh = (await h.Status.GetEventsAsync(Type, "t1")).Single(e => e.EventId == "fresh");
        Assert.True(fresh.Status is ExecutionStatus.Received or ExecutionStatus.Processing);
        gate.SetResult();
        await Eventually(() => h.Position() == 2, "both offsets released");
        Assert.Equal(["fresh"], h.Log.EventIds.ToArray());
    }

    [Fact]
    public async Task Kill_marks_the_task_cancels_its_actor_and_drops_later_events()
    {
        await using var h = new Harness();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Log.OnExecute = async (_, _, ct) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return null; };
        await h.StartAsync();

        h.Source.Push(Event("victim", "running"));
        await started.Task.WaitAsync(Patience);

        h.Source.Push(new TaskEvent { TaskType = Type, TaskId = "victim", EventId = "k", Kind = TaskEventKind.Kill });
        h.Source.Push(Event("victim", "after-kill"));

        await Eventually(() => h.Position() == 3, "all three offsets released");
        Assert.True(await h.Status.IsKilledAsync(Type, "victim"));
        Assert.Equal(ExecutionStatus.Killed, await StatusOf(h.Status, Type, "victim", "running"));
        Assert.Equal(ExecutionStatus.Killed, await StatusOf(h.Status, Type, "victim", "after-kill"));
        Assert.Empty(h.Log.Executed); // the killed run never finished; "after-kill" never started
    }

    [Fact]
    public async Task Events_from_a_killed_producer_are_dropped()
    {
        await using var h = new Harness();
        await h.Status.MarkKilledAsync(Type, "producer");
        await h.StartAsync();

        var child = Event("child", "c0");
        child.ProducerTaskId = "producer";
        h.Source.Push(child);
        h.Source.Push(Event("other", "o0"));

        await Eventually(() => h.Log.EventIds.Contains("o0"), "unrelated event executed");
        await Eventually(() => h.Position() == 2, "both released");
        Assert.Equal(["o0"], h.Log.EventIds.ToArray());
        Assert.Equal(ExecutionStatus.Killed, await StatusOf(h.Status, Type, "child", "c0"));
    }

    [Fact]
    public async Task Self_chained_follow_ups_flow_back_through_the_publisher()
    {
        await using var h = new Harness();
        h.Log.OnExecute = (exec, evt, _) =>
        {
            var n = int.Parse(evt.Content);
            var r = Phanes.Task.Sdk.TaskResults.Ok(exec);
            if (n > 0) r.NextEvents.Add(Phanes.Task.Sdk.TaskResults.Continue(exec, (n - 1).ToString()));
            return Task.FromResult<TaskResult?>(r);
        };
        await h.StartAsync();

        h.Source.Push(Event("count", "start", content: "2"));
        await Eventually(() => h.Publisher.Published.Count == 1, "first follow-up published");
        // The fake publisher is not wired back into the source; feed it manually like Kafka would.
        var hop1 = h.Publisher.Published.Single();
        Assert.Equal("1", hop1.Content);
        Assert.Equal("count", hop1.ProducerTaskId);
        h.Source.Push(hop1);
        await Eventually(() => h.Publisher.Published.Count == 2, "second follow-up published");
        h.Source.Push(h.Publisher.Published.Last());
        await Eventually(() => h.Log.Executed.Count == 3, "three hops executed");
        await Eventually(() => h.Position() == 3, "all released");
    }

    [Fact]
    public async Task Rebalance_forgets_partition_state()
    {
        await using var h = new Harness();
        await h.StartAsync();

        h.Source.Push(Event("t1", "e0"), partition: 3);
        await Eventually(() => h.Log.Executed.Count == 1, "event executed");
        await Eventually(() => h.Position(3) == 1, "position stored");

        int[] before = [], after = [];
        await h.Source.RunOnPollThread(() =>
        {
            before = h.Loop.InitializedPartitions.ToArray();
            h.Source.OnPartitionsRevoked!.Invoke([3]);
            after = h.Loop.InitializedPartitions.ToArray();
        }).WaitAsync(Patience);

        Assert.Equal([3], before);
        Assert.Empty(after);
        Assert.Empty(h.Watermark.Snapshot);
    }

    [Fact]
    public async Task Shutdown_stops_actors_then_closes_the_source()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var h = new Harness();
        var sourceClosedWhileExecuting = false;
        h.Log.OnExecute = async (_, _, ct) =>
        {
            started.TrySetResult();
            try { await release.Task.WaitAsync(ct); }
            finally { sourceClosedWhileExecuting = h.Source.Closed; }
            return null;
        };
        await h.StartAsync();

        h.Source.Push(Event("t1", "e0"));
        await started.Task.WaitAsync(Patience);
        await h.DisposeAsync();

        Assert.True(h.Source.Opened);
        Assert.True(h.Source.Closed);
        Assert.False(sourceClosedWhileExecuting);
        Assert.Equal(-1, h.Position()); // interrupted event not released → redelivered next start
    }

    /// <summary>Hands out an already-retired actor first — the retire-between-lookup-and-enqueue window.</summary>
    private sealed class StaleFirstRegistry(ITaskActor stale, ITaskActor live) : IActorRegistry
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public ITaskActor GetOrCreateActor(string taskId, string taskType) => Interlocked.Increment(ref _calls) == 1 ? stale : live;
        public ITaskActor? TryGetRunning(string taskId, string taskType) => live.IsRunning ? live : null;
        public int CleanupCompletedActors() => 0;
        public int RunningCount(string taskType) => 0;
        public Task CleanupAsync() => Task.CompletedTask;
    }

    [Fact]
    public async Task Event_rejected_by_an_actor_that_retired_mid_dispatch_is_redelivered_to_a_fresh_one()
    {
        var inner = new Harness(); // provides services; its own loop is never started
        TaskActor NewActor() => (TaskActor)inner.Registry.GetOrCreateActor("t1", Type);

        var stale = NewActor();
        await inner.Registry.CleanupAsync(); // retires it (and clears the registry)
        Assert.False(stale.IsRunning);

        var outer = new Harness();
        var live = (TaskActor)outer.Registry.GetOrCreateActor("t1", Type);
        var registry = new StaleFirstRegistry(stale, live);
        await using var h = new Harness(registry);
        await h.StartAsync();

        h.Source.Push(Event("t1", "e0"));
        await Eventually(() => outer.Log.EventIds.Contains("e0"), "event redelivered and executed");
        Assert.Equal(2, registry.Calls);
        await outer.Registry.CleanupAsync();
    }
}
