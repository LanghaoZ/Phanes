namespace Phanes.Task.Core.Options;

/// <summary>
/// Actor lifecycle configuration. Maps to "PhanesTask:Worker" in appsettings.
/// </summary>
public sealed class WorkerOptions
{
    public double ActorIdleTimeoutSeconds { get; init; } = 300;
    public double ActorScanIntervalSeconds { get; init; } = 30;
    public int ActorMailboxCapacity { get; init; } = 64;
    public int WatermarkGapTimeoutSeconds { get; init; } = 60;
}
