using Microsoft.Extensions.Logging.Abstractions;
using Phanes.Task.Core.Actor;
using Phanes.Task.Core.Consumer;
using Phanes.Task.Core.Options;
using static CoreTests.TestUtil;

namespace CoreTests;

public class TaskLoopTests
{
    private sealed class Harness : IAsyncDisposable
    {
        public FakeEventSource Source { get; } = new();
        public CountingRegistry Registry { get; }
        public TaskLoop Loop { get; }

        public Harness(params FakeTask[] tasks)
        {
            var options = Microsoft.Extensions.Options.Options.Create(new TaskOptions
            {
                Kafka = new KafkaOptions { PollTimeoutMs = 5 },
                Worker = new WorkerOptions { ActorScanIntervalSeconds = 0.01 },
            });
            Registry = new CountingRegistry(
                new ActorRegistry(new FakeTaskRegistry(tasks), options, NullLoggerFactory.Instance));
            Loop = new TaskLoop(options, Source, Registry, NullLogger<TaskLoop>.Instance);
        }

        public Task StartAsync() => Loop.StartAsync(default);

        public async ValueTask DisposeAsync()
        {
            await Loop.StopAsync(default).WaitAsync(Patience);
            Loop.Dispose();
        }
    }

    /// <summary>Pass-through registry that counts housekeeping scans.</summary>
    private sealed class CountingRegistry(IActorRegistry inner) : IActorRegistry
    {
        private int _scans;
        public int Scans => Volatile.Read(ref _scans);

        public ITaskActor GetOrCreateActor(string taskId, string taskType)
            => inner.GetOrCreateActor(taskId, taskType);

        public int CleanupCompletedActors()
        {
            Interlocked.Increment(ref _scans);
            return inner.CleanupCompletedActors();
        }

        public int RunningCount(string taskType) => inner.RunningCount(taskType);
        public Task CleanupAsync() => inner.CleanupAsync();
    }

    [Fact]
    public async Task Dispatches_events_to_tasks_and_stores_their_offsets()
    {
        var task = new FakeTask();
        await using var h = new Harness(task);
        await h.StartAsync();

        h.Source.Push(Event("t1", "e0"));
        h.Source.Push(Event("t2", "e1"));
        h.Source.Push(Event("t1", "e2"));

        await Eventually(() => task.Executed.Count == 3, "all events executed");
        await Eventually(() => h.Source.StoredOffsets.Length == 3, "all offsets stored");
        Assert.Equal(new long[] { 0, 1, 2 }, h.Source.StoredOffsets);
    }

    [Fact]
    public async Task Skips_undecodable_and_incomplete_messages_without_stalling()
    {
        var task = new FakeTask();
        await using var h = new Harness(task);
        await h.StartAsync();

        h.Source.Push(new byte[] { 0x0A, 0x05, 0x01 });          // truncated proto
        h.Source.Push((byte[]?)null);                             // empty
        h.Source.Push(Event(taskId: "", eventId: "no-task-id"));  // missing field
        h.Source.Push(Event("t1", "good"));

        await Eventually(() => task.Executed.Contains("good"), "valid event executed");
        await Eventually(() => h.Source.StoredOffsets.Length == 4, "every offset stored");
        Assert.Equal(new[] { "good" }, task.Executed.ToArray());
    }

    [Fact]
    public async Task Skips_events_for_unregistered_task_types()
    {
        var task = new FakeTask();
        await using var h = new Harness(task);
        await h.StartAsync();

        h.Source.Push(Event("t1", "orphan", taskType: "not.registered"));
        h.Source.Push(Event("t1", "good"));

        await Eventually(() => task.Executed.Contains("good"), "valid event executed");
        Assert.Equal(new[] { "good" }, task.Executed.ToArray());
    }

    [Fact]
    public async Task Events_for_a_task_whose_actor_keeps_retiring_are_all_delivered()
    {
        // Finished after every event: each event meets a retired (or retiring)
        // actor and must be delivered through the recreate path.
        var task = new FakeTask { Finished = () => true };
        await using var h = new Harness(task);
        await h.StartAsync();

        for (var i = 0; i < 50; i++)
        {
            h.Source.Push(Event("t1", $"e{i}"));
        }

        await Eventually(() => task.Executed.Count == 50, "all events executed");
        Assert.Equal(Enumerable.Range(0, 50).Select(i => $"e{i}"), task.Executed.ToArray());
        Assert.Equal(1, task.MaxConcurrency);
    }

    /// <summary>
    /// Hands out an actor that has already retired on the first call — the
    /// window where an actor retires between GetOrCreateActor and the enqueue.
    /// </summary>
    private sealed class StaleFirstRegistry(ITaskActor stale, ITaskActor live) : IActorRegistry
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public ITaskActor GetOrCreateActor(string taskId, string taskType)
            => Interlocked.Increment(ref _calls) == 1 ? stale : live;

        public int CleanupCompletedActors() => 0;
        public int RunningCount(string taskType) => 0;
        public Task CleanupAsync() => Task.CompletedTask;
    }

    [Fact]
    public async Task Event_rejected_by_an_actor_that_retired_mid_dispatch_is_redelivered_to_a_fresh_one()
    {
        var task = new FakeTask();
        var registration = new Phanes.Task.Core.Registry.TaskRegistration(task, new FakeContext());
        TaskActor NewActor() => new(
            "t1", task.TaskType, registration, 8, TimeSpan.FromMinutes(5), NullLogger<TaskActor>.Instance);

        var stale = NewActor();
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await stale.RunAsync(cancelled.Token);
        }
        Assert.False(stale.IsRunning);

        var live = NewActor();
        using var liveCts = new CancellationTokenSource();
        var liveRun = live.RunAsync(liveCts.Token);

        var registry = new StaleFirstRegistry(stale, live);
        var source = new FakeEventSource();
        var options = Microsoft.Extensions.Options.Options.Create(new TaskOptions
        {
            Kafka = new KafkaOptions { PollTimeoutMs = 5 },
        });
        using var loop = new TaskLoop(options, source, registry, NullLogger<TaskLoop>.Instance);
        await loop.StartAsync(default);

        source.Push(Event("t1", "e0"));

        await Eventually(() => task.Executed.Contains("e0"), "event redelivered and executed");
        Assert.Equal(2, registry.Calls);

        await loop.StopAsync(default).WaitAsync(Patience);
        liveCts.Cancel();
        await liveRun.WaitAsync(Patience);
    }

    [Fact]
    public async Task Runs_actor_housekeeping_on_the_loop()
    {
        await using var h = new Harness(new FakeTask());
        await h.StartAsync();

        await Eventually(() => h.Registry.Scans >= 2, "cleanup scans ran");
    }

    [Fact]
    public async Task Rebalance_forgets_partition_state()
    {
        var task = new FakeTask();
        await using var h = new Harness(task);
        await h.StartAsync();

        h.Source.Push(Event("t1", "e0"), partition: 3);
        await Eventually(() => task.Executed.Count == 1, "event executed");

        int[] before = [];
        int[] after = [];
        await h.Source.RunOnPollThread(() =>
        {
            before = h.Loop.InitializedPartitions.ToArray();
            h.Source.OnPartitionsRevoked!.Invoke([3]);
            after = h.Loop.InitializedPartitions.ToArray();
        }).WaitAsync(Patience);

        Assert.Equal(new[] { 3 }, before);
        Assert.Empty(after);
    }

    [Fact]
    public async Task Shutdown_stops_actors_then_closes_the_source()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceClosedWhileExecuting = false;
        FakeEventSource? source = null;
        var task = new FakeTask
        {
            OnExecute = async (_, ct) =>
            {
                started.TrySetResult();
                try
                {
                    await release.Task.WaitAsync(ct);
                }
                finally
                {
                    sourceClosedWhileExecuting = source!.Closed;
                }
            },
        };
        var h = new Harness(task);
        source = h.Source;
        await h.StartAsync();

        h.Source.Push(Event("t1", "e0"));
        await started.Task.WaitAsync(Patience);
        await h.DisposeAsync();

        Assert.True(h.Source.Opened);
        Assert.True(h.Source.Closed);
        Assert.False(sourceClosedWhileExecuting);
    }
}
