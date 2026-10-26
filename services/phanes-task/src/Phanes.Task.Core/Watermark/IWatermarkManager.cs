using Phanes.Task.Core.Model;

namespace Phanes.Task.Core.Watermark;

using System.Threading.Tasks;

/// <summary>
/// Per-partition commit cursor: offsets complete out of order, Kafka commits
/// one contiguous position. Completed offsets go on a min-heap; the cursor
/// advances over the contiguous prefix and the loop stores that position.
///
/// THREADING: Notify* are the only thread-safe members (actors call them;
/// they enqueue, never block, never drop). Everything else runs on the Task
/// Loop thread, which drains the queue inside <see cref="Advance"/>. The
/// heaps therefore need no locks.
///
/// Phase 1 never force-advances: a gap with no live owner is REPORTED as
/// stuck, nothing is skipped. Phase 2 adds gap persistence + forced advance.
/// </summary>
public interface IWatermarkManager
{
    /// <summary>An actor is about to execute this offset (owns the gap it may create).</summary>
    void NotifyInFlight(int partition, long offset);

    /// <summary>This offset needs no further processing: executed, skipped, or failed.</summary>
    void NotifyCompletion(int partition, long offset);

    /// <summary>First message seen on a (re)claimed partition: cursor = firstOffset - 1.</summary>
    void InitializePartition(int partition, long firstOffset);

    /// <summary>Partition revoked: drop all state; another consumer owns it now.</summary>
    void ForgetPartition(int partition);

    /// <summary>
    /// Drain notifications, advance cursors, evaluate stuck partitions.
    /// Returns (partition, nextOffsetToRead) for every cursor that moved.
    /// </summary>
    IReadOnlyList<(int Partition, long NextOffset)> Advance(DateTime utcNow);

    /// <summary>Last published snapshot; safe to read from any thread.</summary>
    IReadOnlyList<WatermarkStatus> Snapshot { get; }
}
