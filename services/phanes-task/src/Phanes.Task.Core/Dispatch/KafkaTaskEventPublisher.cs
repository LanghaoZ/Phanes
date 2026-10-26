using Confluent.Kafka;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phanes.Task.Core.Options;

namespace Phanes.Task.Core.Dispatch;

using System.Threading.Tasks;

public sealed class KafkaTaskEventPublisher : ITaskEventPublisher, IDisposable
{
    private readonly KafkaOptions _kafka;
    private readonly Lazy<IProducer<string, byte[]>> _producer;

    public KafkaTaskEventPublisher(IOptions<TaskOptions> options, ILogger<KafkaTaskEventPublisher> logger)
    {
        _kafka = options.Value.Kafka;
        _producer = new Lazy<IProducer<string, byte[]>>(() =>
            new ProducerBuilder<string, byte[]>(new ProducerConfig
                {
                    BootstrapServers = _kafka.BootstrapServers,
                    Acks = Acks.All,
                    EnableIdempotence = true,
                })
                .SetErrorHandler((_, e) => logger.LogError(
                    "Kafka producer error: code={Code} reason={Reason}", e.Code, e.Reason))
                .Build());
    }

    public async Task PublishAsync(TaskEvent evt, CancellationToken ct)
    {
        await _producer.Value.ProduceAsync(
            _kafka.Topic,
            new Message<string, byte[]> { Key = evt.TaskId, Value = evt.ToByteArray() },
            ct);
    }

    public void Dispose()
    {
        if (_producer.IsValueCreated)
        {
            _producer.Value.Flush(TimeSpan.FromSeconds(10));
            _producer.Value.Dispose();
        }
    }
}
