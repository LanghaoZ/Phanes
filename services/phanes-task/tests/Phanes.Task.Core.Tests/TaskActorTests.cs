using Microsoft.Extensions.Logging.Abstractions;
using Phanes.Task.Core.Actor;
using Phanes.Task.Core.Registry;
using static CoreTests.TestUtil;

namespace CoreTests;

public class TaskActorTests
{
    private static TaskActor NewActor(FakeTask task, int capacity = 8, TimeSpan? idle = null)
        => new(
            "task-1",
            task.TaskType,
            new TaskRegistration(task, new FakeContext()),
            capacity,
            idle ?? TimeSpan.FromMinutes(5),
            NullLogger<TaskActor>.Instance);

    [Fact]
    public async Task Processes_events_one_at_a_time_in_order()
    {
        var task = new FakeTask { OnExecute = (_, ct) => Task.Delay(2, ct) };
        var actor = NewActor(task);
        using var cts = new CancellationTokenSource();
        var run = actor.RunAsync(cts.Token);

        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(EnqueueResult.Ok, await actor.EnqueueAsync(Envelope("task-1", $"e{i}"), default));
        }

        await Eventually(() => task.Executed.Count == 6, "all events executed");
        Assert.Equal(new[] { "e0", "e1", "e2", "e3", "e4", "e5" }, task.Executed.ToArray());
        Assert.Equal(1, task.MaxConcurrency);

        cts.Cancel();
        await run.WaitAsync(Patience);
    }

    [Fact]
    public async Task Retires_when_task_reports_finished_and_rejects_later_events()
    {
        var task = new FakeTask { Finished = () => true };
        var actor = NewActor(task);
        var run = actor.RunAsync(default);

        Assert.Equal(EnqueueResult.Ok, await actor.EnqueueAsync(Envelope("task-1", "e0"), default));
        await run.WaitAsync(Patience);

        Assert.True(actor.IsCompleted);
        Assert.False(actor.IsRunning);
        Assert.Equal(EnqueueResult.NotRunning, await actor.EnqueueAsync(Envelope("task-1", "e1"), default));
        Assert.Equal(new[] { "e0" }, task.Executed.ToArray());
    }

    [Fact]
    public async Task Finished_task_still_drains_events_already_buffered()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = new FakeTask
        {
            Finished = () => true,
            OnExecute = async (evt, _) =>
            {
                if (evt.EventId == "e0")
                {
                    started.TrySetResult();
                    await release.Task;
                }
            },
        };
        var actor = NewActor(task);
        var run = actor.RunAsync(default);

        await actor.EnqueueAsync(Envelope("task-1", "e0"), default);
        await started.Task.WaitAsync(Patience);
        await actor.EnqueueAsync(Envelope("task-1", "e1"), default);
        await actor.EnqueueAsync(Envelope("task-1", "e2"), default);
        release.SetResult();

        await run.WaitAsync(Patience);
        Assert.Equal(new[] { "e0", "e1", "e2" }, task.Executed.ToArray());
    }

    [Fact]
    public async Task Retires_itself_after_idle_timeout()
    {
        var actor = NewActor(new FakeTask(), idle: TimeSpan.FromMilliseconds(30));

        await actor.RunAsync(default).WaitAsync(Patience);

        Assert.True(actor.IsCompleted);
        Assert.Equal(EnqueueResult.NotRunning, await actor.EnqueueAsync(Envelope("task-1", "e0"), default));
    }

    [Fact]
    public async Task Failing_execution_does_not_stop_the_actor()
    {
        var task = new FakeTask
        {
            OnExecute = (evt, _) => evt.EventId == "bad"
                ? throw new InvalidOperationException("boom")
                : Task.CompletedTask,
        };
        var actor = NewActor(task);
        using var cts = new CancellationTokenSource();
        var run = actor.RunAsync(cts.Token);

        await actor.EnqueueAsync(Envelope("task-1", "bad"), default);
        await actor.EnqueueAsync(Envelope("task-1", "good"), default);

        await Eventually(() => task.Executed.Contains("good"), "event after the failure executed");
        Assert.True(actor.IsRunning);

        cts.Cancel();
        await run.WaitAsync(Patience);
    }

    [Fact]
    public async Task Fault_outside_execute_retires_the_actor_instead_of_leaving_a_zombie()
    {
        var task = new FakeTask { Finished = () => throw new InvalidOperationException("boom") };
        var actor = NewActor(task);
        var run = actor.RunAsync(default);

        await actor.EnqueueAsync(Envelope("task-1", "e0"), default);
        await run.WaitAsync(Patience);

        Assert.True(actor.IsCompleted);
        Assert.Equal(EnqueueResult.NotRunning, await actor.EnqueueAsync(Envelope("task-1", "e1"), default));
    }

    [Fact]
    public async Task Full_mailbox_makes_enqueue_wait_and_never_drops()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = new FakeTask
        {
            OnExecute = async (_, _) =>
            {
                started.TrySetResult();
                await release.Task;
            },
        };
        var actor = NewActor(task, capacity: 1);
        using var cts = new CancellationTokenSource();
        var run = actor.RunAsync(cts.Token);

        await actor.EnqueueAsync(Envelope("task-1", "e0"), default); // being executed
        await started.Task.WaitAsync(Patience);
        await actor.EnqueueAsync(Envelope("task-1", "e1"), default); // fills the mailbox

        var blocked = actor.EnqueueAsync(Envelope("task-1", "e2"), default).AsTask();
        await Task.Delay(100);
        Assert.False(blocked.IsCompleted);

        release.SetResult();
        Assert.Equal(EnqueueResult.Ok, await blocked.WaitAsync(Patience));
        await Eventually(() => task.Executed.Count == 3, "all three events executed");

        cts.Cancel();
        await run.WaitAsync(Patience);
    }

    [Fact]
    public async Task Enqueue_racing_idle_retirement_never_loses_an_accepted_event()
    {
        var random = new Random(42);
        var accepted = 0;
        var rejected = 0;

        for (var i = 0; i < 300; i++)
        {
            var task = new FakeTask();
            var actor = NewActor(task, idle: TimeSpan.FromMilliseconds(1));
            var run = Task.Run(() => actor.RunAsync(default));

            await Task.Delay(random.Next(0, 3));
            var result = await actor.EnqueueAsync(Envelope("task-1", "e0"), default);

            await run.WaitAsync(Patience); // idles out again after draining
            if (result == EnqueueResult.Ok)
            {
                accepted++;
                Assert.Single(task.Executed);
            }
            else
            {
                rejected++;
                Assert.Empty(task.Executed);
            }
        }

        // Both outcomes must actually occur for the test to mean anything.
        Assert.True(accepted > 0, "no enqueue ever won the race");
        Assert.True(rejected > 0, "no retirement ever won the race");
    }

    [Fact]
    public async Task Cancellation_stops_the_run_loop()
    {
        var actor = NewActor(new FakeTask());
        using var cts = new CancellationTokenSource();
        var run = actor.RunAsync(cts.Token);

        cts.Cancel();
        await run.WaitAsync(Patience);

        Assert.True(actor.IsCompleted);
    }
}
