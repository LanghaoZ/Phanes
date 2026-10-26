using Microsoft.Extensions.Logging.Abstractions;
using Phanes.Task.Contracts;
using Phanes.Task.Core.Actor;
using Phanes.Task.Core.Dispatch;
using Phanes.Task.Core.Registry;
using Phanes.Task.Core.Status;
using Phanes.Task.Core.Watermark;
using Phanes.Task.Sdk;
using static CoreTests.TestUtil;

namespace CoreTests;

public class TaskActorTests
{
    private const string Type = "test.fake";

    private sealed class Harness
    {
        public ExecutionLog Log { get; } = new();
        public InMemoryExecutionStatusStore Status { get; } = new();
        public FakePublisher Publisher { get; } = new();
        public WatermarkManager Watermark { get; } = TestUtil.Watermark();
        public TaskDispatcher Dispatcher { get; }
        public FakeScopeFactory Scopes { get; }
        public ActorServices Services { get; }

        public Harness(double idleSeconds = 300, int mailbox = 8, int maxChainDepth = 64)
        {
            Dispatcher = new TaskDispatcher(Publisher, Options(), NullLogger<TaskDispatcher>.Instance);
            Scopes = new FakeScopeFactory(Log, Type);
            Services = new ActorServices(Status, Dispatcher, Watermark, FastWorker(idleSeconds, mailbox, maxChainDepth));
        }

        public TaskActor NewActor(string taskId = "task-1")
        {
            var descriptor = new TaskTypeDescriptor(Type, typeof(FakeTask), new FakeContext(Type), NullLogger.Instance);
            return new TaskActor(taskId, descriptor, Scopes.Create(Type, taskId), Services, NullLogger<TaskActor>.Instance);
        }

        /// <summary>Drain watermark notifications; returns released (completed) offsets for partition 0.</summary>
        public long Cursor()
        {
            Watermark.InitializePartition(0, 0);
            Watermark.Advance(DateTime.UtcNow);
            return Watermark.Snapshot.Single(s => s.Partition == 0).CommittedOffset;
        }
    }

    // ------------------------------------------------------------ lifecycle

    [Fact]
    public async Task Processes_events_one_at_a_time_in_order()
    {
        var h = new Harness();
        h.Log.OnExecute = async (_, _, ct) => { await Task.Delay(2, ct); return null; };
        var actor = h.NewActor();
        using var cts = new CancellationTokenSource();
        var run = actor.RunAsync(cts.Token);

        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(EnqueueResult.Ok, await actor.EnqueueAsync(Envelope("task-1", $"e{i}", offset: i), default));
        }

        await Eventually(() => h.Log.Executed.Count == 6, "all events executed");
        Assert.Equal(["e0", "e1", "e2", "e3", "e4", "e5"], h.Log.EventIds.ToArray());
        Assert.Equal(1, h.Log.MaxConcurrency);
        cts.Cancel();
        await run.WaitAsync(Patience);
    }

    [Fact]
    public async Task Retires_when_task_reports_finished_and_rejects_later_events()
    {
        var h = new Harness();
        h.Log.Finished = _ => true;
        var actor = h.NewActor();
        var run = actor.RunAsync(default);

        Assert.Equal(EnqueueResult.Ok, await actor.EnqueueAsync(Envelope("task-1", "e0"), default));
        await run.WaitAsync(Patience);

        Assert.True(actor.IsCompleted);
        Assert.Equal(EnqueueResult.NotRunning, await actor.EnqueueAsync(Envelope("task-1", "e1"), default));
        Assert.True(h.Scopes.Created.Single().Disposed);
    }

    [Fact]
    public async Task Finished_task_still_drains_events_already_buffered()
    {
        var h = new Harness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Log.Finished = _ => true;
        h.Log.OnExecute = async (_, evt, _) =>
        {
            if (evt.EventId == "e0") { started.TrySetResult(); await release.Task; }
            return null;
        };
        var actor = h.NewActor();
        var run = actor.RunAsync(default);

        await actor.EnqueueAsync(Envelope("task-1", "e0"), default);
        await started.Task.WaitAsync(Patience);
        await actor.EnqueueAsync(Envelope("task-1", "e1", 1), default);
        await actor.EnqueueAsync(Envelope("task-1", "e2", 2), default);
        release.SetResult();

        await run.WaitAsync(Patience);
        Assert.Equal(["e0", "e1", "e2"], h.Log.EventIds.ToArray());
    }

    [Fact]
    public async Task Retires_itself_after_idle_timeout()
    {
        var h = new Harness(idleSeconds: 0.03);
        var actor = h.NewActor();
        await actor.RunAsync(default).WaitAsync(Patience);
        Assert.True(actor.IsCompleted);
        Assert.Equal(EnqueueResult.NotRunning, await actor.EnqueueAsync(Envelope("task-1", "e0"), default));
    }

    [Fact]
    public async Task Fault_outside_execute_retires_the_actor_instead_of_leaving_a_zombie()
    {
        var h = new Harness();
        h.Log.Finished = _ => throw new InvalidOperationException("boom");
        var actor = h.NewActor();
        var run = actor.RunAsync(default);

        await actor.EnqueueAsync(Envelope("task-1", "e0"), default);
        await run.WaitAsync(Patience);
        Assert.True(actor.IsCompleted);
    }

    [Fact]
    public async Task Full_mailbox_makes_enqueue_wait_and_never_drops()
    {
        var h = new Harness(mailbox: 1);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Log.OnExecute = async (_, _, _) => { started.TrySetResult(); await release.Task; return null; };
        var actor = h.NewActor();
        using var cts = new CancellationTokenSource();
        var run = actor.RunAsync(cts.Token);

        await actor.EnqueueAsync(Envelope("task-1", "e0", 0), default);
        await started.Task.WaitAsync(Patience);
        await actor.EnqueueAsync(Envelope("task-1", "e1", 1), default);
        var blocked = actor.EnqueueAsync(Envelope("task-1", "e2", 2), default).AsTask();
        await Task.Delay(100);
        Assert.False(blocked.IsCompleted);

        release.SetResult();
        Assert.Equal(EnqueueResult.Ok, await blocked.WaitAsync(Patience));
        await Eventually(() => h.Log.Executed.Count == 3, "all three events executed");
        cts.Cancel();
        await run.WaitAsync(Patience);
    }

    [Fact]
    public async Task Enqueue_racing_idle_retirement_never_loses_an_accepted_event()
    {
        var random = new Random(42);
        int accepted = 0, rejected = 0;
        for (var i = 0; i < 200; i++)
        {
            var h = new Harness(idleSeconds: 0.001);
            var actor = h.NewActor();
            var run = Task.Run(() => actor.RunAsync(default));
            await Task.Delay(random.Next(0, 3));
            var result = await actor.EnqueueAsync(Envelope("task-1", "e0"), default);
            await run.WaitAsync(Patience);
            if (result == EnqueueResult.Ok) { accepted++; Assert.Single(h.Log.Executed); }
            else { rejected++; Assert.Empty(h.Log.Executed); }
        }
        Assert.True(accepted > 0, "no enqueue ever won the race");
        Assert.True(rejected > 0, "no retirement ever won the race");
    }

    // --------------------------------------------------------- status/offsets

    [Fact]
    public async Task Success_records_processing_then_completed_and_releases_the_offset()
    {
        var h = new Harness();
        h.Log.OnExecute = (exec, _, _) => Task.FromResult<TaskResult?>(TaskResults.Ok(exec, "did_it"));
        h.Log.Finished = _ => true;
        var actor = h.NewActor();
        var run = actor.RunAsync(default);
        h.Watermark.InitializePartition(0, 0);

        await actor.EnqueueAsync(Envelope("task-1", "e0", offset: 0), default);
        await run.WaitAsync(Patience);

        var record = (await h.Status.GetEventsAsync(Type, "task-1")).Single();
        Assert.Equal(ExecutionStatus.Completed, record.Status);
        Assert.Equal("did_it", record.CompletionReason);
        Assert.Equal(0, h.Cursor());
    }

    [Fact]
    public async Task Exception_and_not_ok_both_record_failed_and_still_release_the_offset()
    {
        var h = new Harness();
        h.Log.OnExecute = (exec, evt, _) => evt.EventId switch
        {
            "throw" => throw new InvalidOperationException("boom"),
            "notok" => Task.FromResult<TaskResult?>(TaskResults.Fail(exec, "nope")),
            _ => Task.FromResult<TaskResult?>(null),
        };
        var actor = h.NewActor();
        using var cts = new CancellationTokenSource();
        var run = actor.RunAsync(cts.Token);
        h.Watermark.InitializePartition(0, 0);

        await actor.EnqueueAsync(Envelope("task-1", "throw", 0), default);
        await actor.EnqueueAsync(Envelope("task-1", "notok", 1), default);
        await actor.EnqueueAsync(Envelope("task-1", "fine", 2), default);
        await Eventually(() => h.Log.EventIds.Contains("fine"), "the fine event executed");
        await Eventually(() => h.Status.GetEventsAsync(Type, "task-1").Result.All(r => r.Status.IsTerminal()), "all three terminal");

        var byId = (await h.Status.GetEventsAsync(Type, "task-1")).ToDictionary(r => r.EventId);
        Assert.Equal(ExecutionStatus.Failed, byId["throw"].Status);
        Assert.Contains("boom", byId["throw"].Error);
        Assert.Equal(ExecutionStatus.Failed, byId["notok"].Status);
        Assert.Equal("nope", byId["notok"].Error);
        Assert.Equal(ExecutionStatus.Completed, byId["fine"].Status);
        Assert.Equal(2, h.Cursor());
        Assert.True(actor.IsRunning);
        cts.Cancel();
        await run.WaitAsync(Patience);
    }

    [Fact]
    public async Task Already_terminal_event_is_skipped_as_a_duplicate_but_its_offset_is_released()
    {
        var h = new Harness();
        await h.Status.TrySetAsync(Type, "task-1", "dup", ExecutionStatus.Completed);
        var actor = h.NewActor();
        using var cts = new CancellationTokenSource();
        var run = actor.RunAsync(cts.Token);
        h.Watermark.InitializePartition(0, 0);

        await actor.EnqueueAsync(Envelope("task-1", "dup", 0), default);
        await Eventually(() => h.Cursor() == 0, "offset released");
        Assert.Empty(h.Log.Executed);
        cts.Cancel();
        await run.WaitAsync(Patience);
    }

    [Fact]
    public async Task Shutdown_mid_execution_leaves_the_event_non_terminal_and_unreleased()
    {
        var h = new Harness();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Log.OnExecute = async (_, _, ct) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return null; };
        var actor = h.NewActor();
        using var cts = new CancellationTokenSource();
        var run = actor.RunAsync(cts.Token);
        h.Watermark.InitializePartition(0, 0);

        await actor.EnqueueAsync(Envelope("task-1", "e0", 0), default);
        await started.Task.WaitAsync(Patience);
        cts.Cancel();
        await run.WaitAsync(Patience);

        Assert.Equal(ExecutionStatus.Processing, await StatusOf(h.Status, Type, "task-1", "e0"));
        Assert.Equal(-1, h.Cursor()); // nothing released → Kafka redelivers
    }

    // ------------------------------------------------------------ continuation

    [Fact]
    public async Task Follow_ups_are_published_with_lineage_before_the_terminal_status()
    {
        var h = new Harness();
        var order = new List<string>();
        h.Log.OnExecute = (exec, evt, _) =>
        {
            var r = TaskResults.Ok(exec);
            r.NextEvents.Add(TaskResults.Continue(exec, "again"));            // self-chain
            r.NextEvents.Add(TaskResults.Spawn("test.other", "child-1", "hi")); // cross-task
            return Task.FromResult<TaskResult?>(r);
        };
        h.Log.Finished = _ => true;
        var actor = h.NewActor();
        var run = actor.RunAsync(default);

        var source = Event("task-1", "e0");
        source.ChainDepth = 3;
        source.RootTaskId = "root"; source.RootTaskType = "test.root";
        await actor.EnqueueAsync(new(source, 0, 0), default);
        await run.WaitAsync(Patience);

        var published = h.Publisher.Published.ToArray();
        Assert.Equal(2, published.Length);
        var self = published.Single(e => e.TaskId == "task-1");
        var child = published.Single(e => e.TaskId == "child-1");
        Assert.Equal(3, self.ChainDepth);          // self-chain keeps depth
        Assert.Equal(4, child.ChainDepth);         // cross-task increments
        Assert.All(published, e =>
        {
            Assert.Equal("task-1", e.ProducerTaskId);
            Assert.Equal("task-1", e.ParentTaskId);
            Assert.Equal("root", e.RootTaskId);
            Assert.Equal("test.root", e.RootTaskType);
            Assert.NotEmpty(e.EventId);
        });
        Assert.Equal(ExecutionStatus.Completed, await StatusOf(h.Status, Type, "task-1", "e0"));
    }

    [Fact]
    public async Task Root_defaults_to_self_for_a_root_event()
    {
        var h = new Harness();
        h.Log.OnExecute = (exec, _, _) =>
        {
            var r = TaskResults.Ok(exec);
            r.NextEvents.Add(TaskResults.Continue(exec, "x"));
            return Task.FromResult<TaskResult?>(r);
        };
        h.Log.Finished = _ => true;
        var actor = h.NewActor();
        var run = actor.RunAsync(default);
        await actor.EnqueueAsync(Envelope("task-1", "e0"), default);
        await run.WaitAsync(Patience);

        var child = h.Publisher.Published.Single();
        Assert.Equal("task-1", child.RootTaskId);
        Assert.Equal(Type, child.RootTaskType);
    }

    [Fact]
    public async Task Follow_up_past_max_chain_depth_is_dropped_and_the_source_still_completes()
    {
        var h = new Harness(maxChainDepth: 2);
        h.Log.OnExecute = (exec, _, _) =>
        {
            var r = TaskResults.Ok(exec);
            r.NextEvents.Add(TaskResults.Spawn("test.other", "deep", "x"));
            return Task.FromResult<TaskResult?>(r);
        };
        h.Log.Finished = _ => true;
        var actor = h.NewActor();
        var run = actor.RunAsync(default);
        var source = Event("task-1", "e0"); source.ChainDepth = 2;
        await actor.EnqueueAsync(new(source, 0, 0), default);
        await run.WaitAsync(Patience);

        Assert.Empty(h.Publisher.Published);
        Assert.Equal(ExecutionStatus.Completed, await StatusOf(h.Status, Type, "task-1", "e0"));
    }

    [Fact]
    public async Task Persistent_publish_failure_fails_the_source_durably_and_releases_the_offset()
    {
        var h = new Harness();
        h.Publisher.FailWith = _ => new IOException("broker down");
        h.Log.OnExecute = (exec, _, _) =>
        {
            var r = TaskResults.Ok(exec);
            r.NextEvents.Add(TaskResults.Continue(exec, "x"));
            return Task.FromResult<TaskResult?>(r);
        };
        h.Log.Finished = _ => true;
        var actor = h.NewActor();
        var run = actor.RunAsync(default);
        h.Watermark.InitializePartition(0, 0);
        await actor.EnqueueAsync(Envelope("task-1", "e0", 0), default);
        await run.WaitAsync(Patience);

        var record = (await h.Status.GetEventsAsync(Type, "task-1")).Single();
        Assert.Equal(ExecutionStatus.Failed, record.Status);
        Assert.Equal("continuation_persist_failed", record.Error);
        Assert.Equal(0, h.Cursor());
    }

    [Fact]
    public async Task Publish_failure_caused_by_shutdown_leaves_the_source_for_redelivery()
    {
        var h = new Harness();
        h.Publisher.FailWith = _ => new DispatcherClosedException();
        h.Log.OnExecute = (exec, _, _) =>
        {
            var r = TaskResults.Ok(exec);
            r.NextEvents.Add(TaskResults.Continue(exec, "x"));
            return Task.FromResult<TaskResult?>(r);
        };
        h.Log.Finished = _ => true;
        var actor = h.NewActor();
        var run = actor.RunAsync(default);
        h.Watermark.InitializePartition(0, 0);
        await actor.EnqueueAsync(Envelope("task-1", "e0", 0), default);
        await run.WaitAsync(Patience);

        Assert.Equal(ExecutionStatus.Processing, await StatusOf(h.Status, Type, "task-1", "e0"));
        Assert.Equal(-1, h.Cursor());
    }

    [Fact]
    public async Task Follow_ups_of_a_killed_task_are_dropped_at_source()
    {
        var h = new Harness();
        await h.Status.MarkKilledAsync(Type, "task-1");
        h.Log.OnExecute = (exec, _, _) =>
        {
            var r = TaskResults.Ok(exec);
            r.NextEvents.Add(TaskResults.Continue(exec, "x"));
            return Task.FromResult<TaskResult?>(r);
        };
        h.Log.Finished = _ => true;
        var actor = h.NewActor();
        var run = actor.RunAsync(default);
        await actor.EnqueueAsync(Envelope("task-1", "e0"), default);
        await run.WaitAsync(Patience);

        Assert.Empty(h.Publisher.Published);
        Assert.Equal(ExecutionStatus.Killed, await StatusOf(h.Status, Type, "task-1", "e0")); // terminal overridden
    }

    // ------------------------------------------------------------------- kill

    [Fact]
    public async Task Kill_cancels_the_execution_records_killed_for_everything_buffered_and_releases_offsets()
    {
        var h = new Harness();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Log.OnExecute = async (_, _, ct) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return null; };
        var actor = h.NewActor();
        var run = actor.RunAsync(default);
        h.Watermark.InitializePartition(0, 0);

        await actor.EnqueueAsync(Envelope("task-1", "running", 0), default);
        await started.Task.WaitAsync(Patience);
        await actor.EnqueueAsync(Envelope("task-1", "queued", 1), default);

        actor.RequestKill();
        await run.WaitAsync(Patience);

        Assert.True(actor.IsCompleted);
        Assert.Equal(ExecutionStatus.Killed, await StatusOf(h.Status, Type, "task-1", "running"));
        Assert.Equal(ExecutionStatus.Killed, await StatusOf(h.Status, Type, "task-1", "queued"));
        Assert.Equal(1, h.Cursor());
        Assert.Equal(EnqueueResult.NotRunning, await actor.EnqueueAsync(Envelope("task-1", "late", 2), default));
    }
}
