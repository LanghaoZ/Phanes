using Phanes.Task.Core.Model;

namespace Phanes.Task.Core.Consumer;

/// <summary>
/// Tracks Kafka offset commits per partition using a per-partition min-heap.
/// Ensures the consumer group only advances its committed offset past events
/// that have been fully processed, handling out-of-order completions correctly.
///
/// Phase 1: contiguous drain + force-advance on stuck TTL or heap size cap.
/// Phase 2: at-least-once — persist gap events to Redis before force-advancing.
/// </summary>
public interface IWatermarkManager
{
    /// <summary>
    /// Called by the Task Loop on the first message seen for a partition.
    /// Sets CommittedOffset to firstOffset - 1 so the first completed event
    /// can be committed.
    /// </summary>
    void InitializePartitionOffset(int partition, long firstOffset);

    /// <summary>
    /// Notify that a single event has been fully processed by its actor.
    /// Pushes the offset onto the partition's min-heap for commit evaluation.
    /// </summary>
    void NotifyCompletion(TaskEventCompletionNotification notification);

    /// <summary>
    /// Batch variant of NotifyCompletion. Convenience for bulk notifications.
    /// </summary>
    void NotifyCompletions(IEnumerable<TaskEventCompletionNotification> notifications);

    /// <summary>
    /// Returns a snapshot of each partition's watermark state.
    /// Used by monitoring endpoints and management APIs.
    /// </summary>
    IDictionary<int, WatermarkStatus> GetWatermarkStatus();
}
