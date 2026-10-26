namespace Phanes.Task.Core.Options;

using System.Threading.Tasks;

/// <summary>Root configuration, bound from the "PhanesTask" section.</summary>
public sealed class TaskOptions
{
    public KafkaOptions      Kafka      { get; set; } = new();
    public WorkerOptions     Worker     { get; set; } = new();
    public RedisOptions      Redis      { get; set; } = new();
    public DispatcherOptions Dispatcher { get; set; } = new();
    public Phanes.Task.Core.Configuration.PhanesConfigOptions ConfigService { get; set; } = new();
}
