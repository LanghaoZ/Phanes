using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phanes.Task.Core.Options;

namespace Phanes.Task.Core.Consumer;

/// <summary>ITaskEventSource backed by a Confluent.Kafka consumer.</summary>
public sealed class KafkaTaskEventSource : ITaskEventSource
{
    private readonly KafkaOptions _kafka;
    private readonly ILogger<KafkaTaskEventSource> _logger;
    private IConsumer<Ignore, byte[]>? _consumer;

    public KafkaTaskEventSource(IOptions<TaskOptions> options, ILogger<KafkaTaskEventSource> logger)
    {
        _kafka = options.Value.Kafka;
        _logger = logger;
    }

    public Action<IReadOnlyList<int>>? OnPartitionsAssigned { get; set; }
    public Action<IReadOnlyList<int>>? OnPartitionsRevoked  { get; set; }

    public void Open()
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _kafka.BootstrapServers,
            GroupId = _kafka.ConsumerGroup,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true,
            // Offsets are stored explicitly by the loop; the client's
            // auto-commit flushes stored offsets on its own cadence (5s).
            EnableAutoOffsetStore = false,
            EnablePartitionEof = false,
        };

        _consumer = new ConsumerBuilder<Ignore, byte[]>(config)
            .SetErrorHandler((_, e) => _logger.LogError(
                "Kafka client error: code={Code} reason={Reason} fatal={Fatal}",
                e.Code, e.Reason, e.IsFatal))
            .SetLogHandler((_, m) => _logger.LogDebug(
                "Kafka client log: facility={Facility} level={Level} message={Message}",
                m.Facility, m.Level, m.Message))
            // Both handlers run on the polling thread, inside Consume().
            .SetPartitionsAssignedHandler((_, partitions) =>
                OnPartitionsAssigned?.Invoke(partitions.Select(p => p.Partition.Value).ToList()))
            .SetPartitionsRevokedHandler((_, partitions) =>
                OnPartitionsRevoked?.Invoke(partitions.Select(p => p.Partition.Value).ToList()))
            .Build();

        _consumer.Subscribe(_kafka.Topic);
        _logger.LogInformation(
            "Subscribed. bootstrap={Bootstrap} topic={Topic} group={Group}",
            _kafka.BootstrapServers, _kafka.Topic, _kafka.ConsumerGroup);
    }

    public ConsumedMessage? Poll(TimeSpan timeout)
    {
        try
        {
            var result = Consumer.Consume(timeout);
            if (result is null)
            {
                return null;
            }
            return new ConsumedMessage(result.Message?.Value, result.Partition.Value, result.Offset.Value);
        }
        catch (ConsumeException ex)
        {
            _logger.LogError(ex, "Kafka consume error: {Reason}", ex.Error.Reason);
            return null;
        }
    }

    public void StoreOffset(ConsumedMessage message)
    {
        // Kafka commits the NEXT offset to read.
        Consumer.StoreOffset(new TopicPartitionOffset(
            _kafka.Topic, new Partition(message.Partition), new Offset(message.Offset + 1)));
    }

    public void Close()
    {
        try
        {
            _consumer?.Close();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while closing Kafka consumer.");
        }
        finally
        {
            _consumer?.Dispose();
            _consumer = null;
        }
    }

    private IConsumer<Ignore, byte[]> Consumer
        => _consumer ?? throw new InvalidOperationException("Open() has not been called.");
}
