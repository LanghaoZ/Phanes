namespace Phanes.Task.Core.Options;

using System.Threading.Tasks;

/// <summary>
/// Kafka client configuration. Maps to "PhanesTask:Kafka" in appsettings.
/// </summary>
public sealed class KafkaOptions
{
    public string BootstrapServers { get; set; } = string.Empty;
    public string Topic { get; set; } = string.Empty;
    public string ConsumerGroup { get; set; } = string.Empty;
    public int PollTimeoutMs { get; set; } = 500;
}
