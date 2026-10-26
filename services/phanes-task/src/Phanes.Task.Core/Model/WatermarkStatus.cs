namespace Phanes.Task.Core.Model;

using System.Threading.Tasks;

/// <summary>Snapshot of one partition's watermark, for monitoring.</summary>
public sealed record WatermarkStatus(
    int Partition,
    long CommittedOffset,
    int PendingCount,
    int InFlightCount,
    bool Stuck);
