namespace Phanes.Task.Core.Model;

/// <summary>
/// Snapshot of a single partition's watermark state, exposed for monitoring and debugging.
/// Returned by IWatermarkManager.GetWatermarkStatus().
/// </summary>
public record WatermarkStatus
{
    public int  Partition          { get; init; }
    public long CommittedOffset    { get; init; }
    public int  PendingCount       { get; init; }
    public long NextExpectedOffset { get; init; }
}
