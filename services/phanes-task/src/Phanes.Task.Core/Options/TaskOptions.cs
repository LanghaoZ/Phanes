namespace Phanes.Task.Core.Options;

/// <summary>
/// Root configuration for the Phanes Task service. Bound from the
/// "PhanesTask" section of appsettings.
///
/// Sub-option classes are added here as the framework's subsystems
/// (Worker, Watermark, Redis, MySQL, Plugins, ...) come online.
/// </summary>
public sealed class TaskOptions
{
    public KafkaOptions  Kafka  { get; init; } = new();
    public WorkerOptions Worker { get; init; } = new();
}
