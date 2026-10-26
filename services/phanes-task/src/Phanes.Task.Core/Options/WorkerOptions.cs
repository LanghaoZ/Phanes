namespace Phanes.Task.Core.Options;

using System.Threading.Tasks;

/// <summary>Actor and loop configuration. Maps to "PhanesTask:Worker".</summary>
public sealed class WorkerOptions
{
    public double ActorIdleTimeoutSeconds  { get; init; } = 300;
    public double ActorScanIntervalSeconds { get; init; } = 30;
    public int    ActorMailboxCapacity     { get; init; } = 64;

    /// <summary>Cross-task follow-up hops allowed before the actor refuses to emit.</summary>
    public int MaxChainDepth { get; init; } = 64;

    /// <summary>How often the loop evaluates the watermark and stores advanced offsets.</summary>
    public double WatermarkCommitIntervalSeconds { get; init; } = 2;

    /// <summary>
    /// A partition whose watermark has not advanced for this long while
    /// completions are pending (and no live actor owns the gap) is reported
    /// as stuck. Phase 1 only reports; it never force-advances.
    /// </summary>
    public double WatermarkStuckSeconds { get; init; } = 300;
}
