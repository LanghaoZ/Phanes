namespace Phanes.Task.Core.Options;

using System.Threading.Tasks;

/// <summary>Root configuration, bound from the "PhanesTask" section.</summary>
public sealed class TaskOptions
{
    public KafkaOptions      Kafka      { get; init; } = new();
    public WorkerOptions     Worker     { get; init; } = new();
    public RedisOptions      Redis      { get; init; } = new();
    public DispatcherOptions Dispatcher { get; init; } = new();
}
