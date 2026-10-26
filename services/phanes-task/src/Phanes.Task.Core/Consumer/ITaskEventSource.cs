namespace Phanes.Task.Core.Consumer;

using System.Threading.Tasks;

/// <summary>One raw message pulled from the event stream.</summary>
public sealed record ConsumedMessage(byte[]? Value, int Partition, long Offset);

/// <summary>
/// The Task Loop's view of the event stream. Thin seam over the Kafka consumer
/// so the loop can be tested without a broker.
///
/// THREADING: used only from the Task Loop thread. Rebalance callbacks are
/// invoked on that same thread, from inside Poll.
/// </summary>
public interface ITaskEventSource
{
    Action<IReadOnlyList<int>>? OnPartitionsAssigned { get; set; }
    Action<IReadOnlyList<int>>? OnPartitionsRevoked  { get; set; }

    /// <summary>Connect and subscribe. Called once before the first Poll.</summary>
    void Open();

    /// <summary>Next message, or null if none arrived within the timeout.</summary>
    ConsumedMessage? Poll(TimeSpan timeout);

    /// <summary>
    /// Record the next offset to read for a partition (everything below it is
    /// done). The client's auto-commit flushes stored positions to the broker.
    /// </summary>
    void StorePosition(int partition, long nextOffset);

    /// <summary>Commit stored offsets and leave the group.</summary>
    void Close();
}
