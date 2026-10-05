using Confluent.Kafka;
using Google.Protobuf;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phanes.Task.Core.Actor;
using Phanes.Task.Core.Options;

namespace Phanes.Task.Core.Consumer;

/// <summary>
/// Walking-skeleton Task Loop. Consumes proto-encoded TaskEvent messages,
/// dispatches to a per-task_id actor, and stores the Kafka offset immediately
/// after dispatch.
///
/// At-most-once semantics until WatermarkManager lands — if the process dies
/// between StoreOffset and the actor finishing the event, the event is lost.
/// This is acceptable for the MVP; once WatermarkManager is wired up, offset
/// commits will move from "after dispatch" to "after actor completes" and
/// gap-aware draining.
/// </summary>
public sealed class TaskLoop : BackgroundService
{
    private readonly KafkaOptions _kafka;
    private readonly WorkerOptions _worker;
    private readonly IActorRegistry _actors;
    private readonly ILogger<TaskLoop> _logger;

    public TaskLoop(
        IOptions<TaskOptions> options,
        IActorRegistry actors,
        ILogger<TaskLoop> logger)
    {
        _kafka = options.Value.Kafka;
        _worker = options.Value.Worker;
        _actors = actors;
        _logger = logger;
    }

    protected override global::System.Threading.Tasks.Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consumeTask = global::System.Threading.Tasks.Task.Run(() => ConsumeLoop(stoppingToken), stoppingToken);
        var cleanupTask = CleanupLoop(stoppingToken);
        return global::System.Threading.Tasks.Task.WhenAll(consumeTask, cleanupTask);
    }

    private void ConsumeLoop(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _kafka.BootstrapServers,
            GroupId = _kafka.ConsumerGroup,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true,
            // Offsets are stored manually after the actor accepts the event;
            // the auto-commit loop flushes stored offsets to Kafka on its
            // own cadence (default 5s).
            EnableAutoOffsetStore = false,
            EnablePartitionEof = false,
        };

        using var consumer = new ConsumerBuilder<Ignore, byte[]>(config)
            .SetErrorHandler((_, e) => _logger.LogError(
                "Kafka client error: code={Code} reason={Reason} fatal={Fatal}",
                e.Code, e.Reason, e.IsFatal))
            .SetLogHandler((_, m) => _logger.LogDebug(
                "Kafka client log: facility={Facility} level={Level} message={Message}",
                m.Facility, m.Level, m.Message))
            .Build();

        consumer.Subscribe(_kafka.Topic);
        _logger.LogInformation(
            "Task loop started. bootstrap={Bootstrap} topic={Topic} group={Group}",
            _kafka.BootstrapServers, _kafka.Topic, _kafka.ConsumerGroup);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<Ignore, byte[]>? result;
                try
                {
                    result = consumer.Consume(stoppingToken);
                }
                catch (ConsumeException ex)
                {
                    _logger.LogError(ex, "Kafka consume error: {Reason}", ex.Error.Reason);
                    continue;
                }

                if (result?.Message?.Value is null)
                {
                    continue;
                }

                TaskEvent evt;
                try
                {
                    evt = TaskEvent.Parser.ParseFrom(result.Message.Value);
                }
                catch (InvalidProtocolBufferException ex)
                {
                    _logger.LogError(ex,
                        "Failed to deserialize TaskEvent. partition={Partition} offset={Offset}",
                        result.Partition.Value, result.Offset.Value);
                    // Skip the bad message — store the offset so we don't loop on it.
                    consumer.StoreOffset(result);
                    continue;
                }

                if (string.IsNullOrEmpty(evt.TaskType) || string.IsNullOrEmpty(evt.TaskId))
                {
                    _logger.LogWarning(
                        "TaskEvent missing required fields. partition={Partition} offset={Offset} task_type={TaskType} task_id={TaskId}",
                        result.Partition.Value, result.Offset.Value, evt.TaskType, evt.TaskId);
                    consumer.StoreOffset(result);
                    continue;
                }

                ITaskActor actor;
                try
                {
                    actor = _actors.GetOrCreateActor(evt.TaskId, evt.TaskType);
                }
                catch (KeyNotFoundException ex)
                {
                    _logger.LogError(ex,
                        "No task registered for task_type. task_type={TaskType} task_id={TaskId}",
                        evt.TaskType, evt.TaskId);
                    consumer.StoreOffset(result);
                    continue;
                }

                if (!actor.TryEnqueue(evt))
                {
                    _logger.LogWarning(
                        "Actor channel full, dropping event. task_id={TaskId} event_id={EventId}",
                        evt.TaskId, evt.EventId);
                }

                // TEMP: at-most-once. Replace with WatermarkManager.NotifyCompletion
                // once actor-driven offset tracking is in place.
                consumer.StoreOffset(result);
            }
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown via stoppingToken
        }
        finally
        {
            try
            {
                consumer.Close();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error while closing Kafka consumer.");
            }
            _logger.LogInformation("Task loop stopped.");
        }
    }

    private async global::System.Threading.Tasks.Task CleanupLoop(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_worker.ActorScanIntervalSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                _actors.CleanupCompletedActors();
            }
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
        await _actors.CleanupAsync();
    }
}
