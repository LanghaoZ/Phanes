using Phanes.Task.Core.Watermark;
using static CoreTests.TestUtil;

namespace CoreTests;

public class WatermarkManagerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Advances_over_the_contiguous_prefix_regardless_of_completion_order()
    {
        var wm = Watermark();
        wm.InitializePartition(0, firstOffset: 10);

        wm.NotifyCompletion(0, 12);
        wm.NotifyCompletion(0, 10);
        Assert.Equal([(0, 11L)], wm.Advance(T0)); // 10 done, 11 missing

        wm.NotifyCompletion(0, 11);
        Assert.Equal([(0, 13L)], wm.Advance(T0)); // 11 fills the gap, 12 follows
        Assert.Empty(wm.Advance(T0));
    }

    [Fact]
    public void Ignores_offsets_at_or_below_the_cursor_and_unknown_partitions()
    {
        var wm = Watermark();
        wm.InitializePartition(0, 5);

        wm.NotifyCompletion(0, 4);   // below cursor
        wm.NotifyCompletion(7, 99);  // unknown partition
        Assert.Empty(wm.Advance(T0));

        wm.NotifyCompletion(0, 5);
        wm.NotifyCompletion(0, 5);   // duplicate
        Assert.Equal([(0, 6L)], wm.Advance(T0));
        Assert.Equal(0, wm.Snapshot.Single().PendingCount);
    }

    [Fact]
    public void Gap_owned_by_a_live_actor_is_not_stuck()
    {
        var wm = Watermark();
        wm.InitializePartition(0, 0);
        wm.NotifyInFlight(0, 0);
        wm.NotifyCompletion(0, 1);

        wm.Advance(T0);
        wm.Advance(T0.AddMinutes(10));

        var status = wm.Snapshot.Single();
        Assert.False(status.Stuck);
        Assert.Equal(1, status.InFlightCount);
        Assert.Equal(1, status.PendingCount);
    }

    [Fact]
    public void Gap_with_no_owner_is_reported_stuck_after_the_threshold_and_never_skipped()
    {
        var worker = FastWorker();
        var wm = Watermark(new Phanes.Task.Core.Options.WorkerOptions
        {
            ActorIdleTimeoutSeconds = worker.ActorIdleTimeoutSeconds,
            ActorScanIntervalSeconds = worker.ActorScanIntervalSeconds,
            WatermarkCommitIntervalSeconds = worker.WatermarkCommitIntervalSeconds,
            WatermarkStuckSeconds = 60,
        });
        wm.InitializePartition(0, 0);
        wm.NotifyCompletion(0, 1); // offset 0 never completes, nobody owns it

        wm.Advance(T0);
        Assert.False(wm.Snapshot.Single().Stuck);
        Assert.Empty(wm.Advance(T0.AddSeconds(61)));
        Assert.True(wm.Snapshot.Single().Stuck);
        Assert.Equal(-1, wm.Snapshot.Single().CommittedOffset); // not force-advanced

        wm.NotifyCompletion(0, 0);
        Assert.Equal([(0, 2L)], wm.Advance(T0.AddSeconds(62)));
        Assert.False(wm.Snapshot.Single().Stuck);
    }

    [Fact]
    public void Forgetting_a_partition_drops_its_state_so_a_reclaim_reinitializes()
    {
        var wm = Watermark();
        wm.InitializePartition(0, 0);
        wm.NotifyCompletion(0, 0);
        wm.Advance(T0);

        wm.ForgetPartition(0);
        Assert.Empty(wm.Snapshot);

        wm.InitializePartition(0, 40); // another consumer moved it meanwhile
        wm.NotifyCompletion(0, 40);
        Assert.Equal([(0, 41L)], wm.Advance(T0));
    }

    [Fact]
    public void Partitions_are_independent()
    {
        var wm = Watermark();
        wm.InitializePartition(0, 0);
        wm.InitializePartition(1, 100);
        wm.NotifyCompletion(1, 100);
        wm.NotifyCompletion(0, 1);

        Assert.Equal([(1, 101L)], wm.Advance(T0));
    }
}
