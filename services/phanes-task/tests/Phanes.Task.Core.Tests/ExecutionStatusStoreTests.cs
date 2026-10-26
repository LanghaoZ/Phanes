using Phanes.Task.Core.Status;

namespace CoreTests;

public class ExecutionStatusStoreTests
{
    private readonly InMemoryExecutionStatusStore _store = new();

    [Fact]
    public async Task Transitions_are_monotonic()
    {
        Assert.Equal(StatusWriteResult.Applied, await _store.TrySetAsync("t", "a", "e", ExecutionStatus.Submitted));
        Assert.Equal(StatusWriteResult.Applied, await _store.TrySetAsync("t", "a", "e", ExecutionStatus.Received));
        Assert.Equal(StatusWriteResult.Stale, await _store.TrySetAsync("t", "a", "e", ExecutionStatus.Submitted));
        Assert.Equal(StatusWriteResult.Applied, await _store.TrySetAsync("t", "a", "e", ExecutionStatus.Processing));
        Assert.Equal(StatusWriteResult.Stale, await _store.TrySetAsync("t", "a", "e", ExecutionStatus.Processing));
    }

    [Fact]
    public async Task First_terminal_status_wins()
    {
        await _store.TrySetAsync("t", "a", "e", ExecutionStatus.Processing);
        Assert.Equal(StatusWriteResult.Applied, await _store.TrySetAsync("t", "a", "e", ExecutionStatus.Failed, "boom"));
        Assert.Equal(StatusWriteResult.AlreadyTerminal, await _store.TrySetAsync("t", "a", "e", ExecutionStatus.Completed));
        Assert.Equal(StatusWriteResult.AlreadyTerminal, await _store.TrySetAsync("t", "a", "e", ExecutionStatus.Processing));

        Assert.True(await _store.IsCompletedAsync("t", "a", "e"));
        var record = (await _store.GetEventsAsync("t", "a")).Single();
        Assert.Equal(ExecutionStatus.Failed, record.Status);
        Assert.Equal("boom", record.Error);
    }

    [Fact]
    public async Task Terminal_can_be_written_directly_and_kill_markers_are_per_task()
    {
        Assert.Equal(StatusWriteResult.Applied, await _store.TrySetAsync("t", "a", "e", ExecutionStatus.Killed));
        Assert.False(await _store.IsKilledAsync("t", "a"));
        await _store.MarkKilledAsync("t", "a");
        Assert.True(await _store.IsKilledAsync("t", "a"));
        Assert.False(await _store.IsKilledAsync("t", "b"));
    }

    [Fact]
    public void Ranks_and_wire_format()
    {
        Assert.True(ExecutionStatus.Killed.IsTerminal());
        Assert.False(ExecutionStatus.Processing.IsTerminal());
        Assert.Equal(ExecutionStatus.Completed.Rank(), ExecutionStatus.Failed.Rank());
        Assert.Equal("failedwithretry", ExecutionStatus.FailedWithRetry.Wire());
        Assert.Equal(ExecutionStatus.Processing, ExecutionStatusExtensions.Parse("processing"));
        Assert.Equal(ExecutionStatus.Unknown, ExecutionStatusExtensions.Parse("nope"));
    }
}
