namespace Phanes.Task.Core.Options;

/// <summary>
/// Kafka client configuration. Maps to "PhanesTask:Kafka" in appsettings.
/// </summary>
public sealed class KafkaOptions
{
    public string BootstrapServers { get; init; } = string.Empty;
    public string Topic { get; init; } = string.Empty;
    public string ConsumerGroup { get; init; } = string.Empty;
}
