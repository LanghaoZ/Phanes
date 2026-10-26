namespace Phanes.Task.Core.Options;

using System.Threading.Tasks;

/// <summary>
/// Kafka client configuration. Maps to "PhanesTask:Kafka" in appsettings.
/// </summary>
public sealed class KafkaOptions
{
    public string BootstrapServers { get; init; } = string.Empty;
    public string Topic { get; init; } = string.Empty;
    public string ConsumerGroup { get; init; } = string.Empty;
    public int PollTimeoutMs { get; init; } = 500;
}
