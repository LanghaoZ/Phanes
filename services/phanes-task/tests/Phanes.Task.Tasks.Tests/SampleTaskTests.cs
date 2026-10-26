using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phanes.Task.Contracts;
using Phanes.Task.Sdk;
using Phanes.Task.Tasks;

namespace TasksTests;

public class SampleTaskTests
{
    private sealed class Context(string type) : ITaskContext
    {
        public string TaskType => type;
        public ILogger Logger => NullLogger.Instance;
        public void Set<T>(string key, T value) where T : notnull { }
        public T Get<T>(string key) where T : notnull => throw new KeyNotFoundException(key);
    }

    private sealed class NoServices : IServiceProvider { public object? GetService(Type t) => null; }

    private static TaskExecution Exec(string type, string taskId = "t1", string eventId = "e1")
        => new(type, taskId, eventId, taskId, type, "", 0, new Context(type), NullLogger.Instance, new NoServices());

    private static TaskEvent Evt(string type, string content, Dictionary<string, string>? ctx = null)
    {
        var e = new TaskEvent { TaskType = type, TaskId = "t1", EventId = "e1", Content = content };
        if (ctx is not null) e.Context.Add(ctx);
        return e;
    }

    [Fact]
    public async Task Echo_completes_after_one_event()
    {
        var task = new EchoTask();
        Assert.False(task.IsFinished());
        var result = await task.ExecuteAsync(Exec(PhanesNativeTaskTypes.Echo), Evt(PhanesNativeTaskTypes.Echo, "hi"), default);
        Assert.True(result.Ok);
        Assert.Equal("echoed", result.CompletionReason);
        Assert.True(task.IsFinished());
    }

    [Fact]
    public async Task Countdown_emits_a_self_chained_follow_up_until_zero()
    {
        var task = new CountdownTask();
        var type = PhanesNativeTaskTypes.Countdown;
        var ctx = new Dictionary<string, string> { ["step_delay_ms"] = "250" };

        var step = await task.ExecuteAsync(Exec(type), Evt(type, "2", ctx), default);
        Assert.True(step.Ok);
        Assert.False(task.IsFinished());
        var next = step.NextEvents.Single();
        Assert.Equal("1", next.Content);
        Assert.Equal("t1", next.TaskId);
        Assert.Equal(250, next.DelayMs);
        Assert.Equal("250", next.Context["step_delay_ms"]);

        var last = await task.ExecuteAsync(Exec(type), Evt(type, "0"), default);
        Assert.Empty(last.NextEvents);
        Assert.Equal("reached_zero", last.CompletionReason);
        Assert.True(task.IsFinished());

        var bad = await task.ExecuteAsync(Exec(type), Evt(type, "nope"), default);
        Assert.False(bad.Ok);
    }

    [Fact]
    public async Task Flaky_fails_on_command()
    {
        var task = new FlakyTask();
        var type = PhanesNativeTaskTypes.Flaky;
        Assert.True((await task.ExecuteAsync(Exec(type), Evt(type, "ok"), default)).Ok);
        Assert.False((await task.ExecuteAsync(Exec(type), Evt(type, "fail"), default)).Ok);
        await Assert.ThrowsAsync<InvalidOperationException>(() => task.ExecuteAsync(Exec(type), Evt(type, "throw"), default));
    }

    [Fact]
    public async Task Slow_honours_cancellation()
    {
        var task = new SlowTask();
        using var cts = new CancellationTokenSource(20);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => task.ExecuteAsync(Exec(PhanesNativeTaskTypes.Slow), Evt(PhanesNativeTaskTypes.Slow, "5000"), cts.Token));
    }
}
