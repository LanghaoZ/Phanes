using Microsoft.Extensions.Logging.Abstractions;
using Phanes.Task.Contracts;
using Phanes.Task.Core.Dispatch;
using Phanes.Task.Core.Options;
using static CoreTests.TestUtil;

namespace CoreTests;

public class TaskDispatcherTests
{
    private readonly FakePublisher _publisher = new();

    private TaskDispatcher NewDispatcher(int maxDelaySeconds = 3600)
        => new(_publisher, Options(dispatcher: new DispatcherOptions { MaxDelaySeconds = maxDelaySeconds }),
            NullLogger<TaskDispatcher>.Instance);

    [Fact]
    public async Task Submit_publishes_immediately()
    {
        await using var d = NewDispatcher();
        await d.SubmitAsync(Event("t1", "e1"), default);
        Assert.Equal("e1", _publisher.Published.Single().EventId);
    }

    [Fact]
    public async Task Delayed_submit_publishes_after_the_delay_and_rejects_too_long()
    {
        await using var d = NewDispatcher(maxDelaySeconds: 1);
        await d.SubmitWithDelayAsync(Event("t1", "later"), TimeSpan.FromMilliseconds(50), default);
        Assert.Empty(_publisher.Published);
        Assert.Equal(1, d.PendingDelayed);

        await Eventually(() => _publisher.Published.Count == 1, "delayed event published");
        Assert.Equal("later", _publisher.Published.Single().EventId);

        await Assert.ThrowsAsync<DelayTooLongException>(
            () => d.SubmitWithDelayAsync(Event("t1", "never"), TimeSpan.FromSeconds(2), default));
    }

    [Fact]
    public async Task Kill_publishes_a_control_event_keyed_to_the_target()
    {
        await using var d = NewDispatcher();
        await d.KillAsync("test.fake", "victim", default);
        var kill = _publisher.Published.Single();
        Assert.Equal(TaskEventKind.Kill, kill.Kind);
        Assert.Equal("victim", kill.TaskId);
        Assert.Equal("test.fake", kill.TaskType);
        Assert.NotEmpty(kill.EventId);
    }

    [Fact]
    public async Task Closed_dispatcher_rejects_new_work_and_drops_pending_timers()
    {
        var d = NewDispatcher();
        await d.SubmitWithDelayAsync(Event("t1", "pending"), TimeSpan.FromSeconds(30), default);
        await d.DisposeAsync();

        Assert.Empty(_publisher.Published);
        await Assert.ThrowsAsync<DispatcherClosedException>(() => d.SubmitAsync(Event("t1", "x"), default));
    }
}
