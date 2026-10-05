namespace Phanes.Task.Core.Options;

/// <summary>
/// Actor lifecycle configuration. Maps to "PhanesTask:Worker" in appsettings.
/// </summary>
public sealed class WorkerOptions
{
    public int ActorIdleTimeoutSeconds   { get; init; } = 300;
    public int ActorScanIntervalSeconds  { get; init; } = 30;
    public int WatermarkGapTimeoutSeconds { get; init; } = 60;
}
