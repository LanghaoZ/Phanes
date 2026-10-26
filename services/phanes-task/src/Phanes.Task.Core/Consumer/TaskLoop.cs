using Google.Protobuf;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phanes.Task.Core.Actor;
using Phanes.Task.Core.Model;
using Phanes.Task.Core.Options;

namespace Phanes.Task.Core.Consumer;

/// <summary>
/// The Task Loop: one dedicated thread that does one thing per iteration —
/// poll an event and dispatch it, or run housekeeping. Rebalance callbacks
/// fire on this same thread (from inside Poll). Because everything the loop
/// owns (actor registry, partition bookkeeping) is touched only here, none of
/// it needs locking.
///
/// The only concurrency boundary is loop → actor: EnqueueAsync is atomic with
/// actor retirement (see TaskActor).
///
/// Offsets: stored right after dispatch, i.e. at-most-once. TEMP until
/// WatermarkManager moves the commit to "after the actor completes".
/// </summary>
public sealed class TaskLoop : BackgroundService
{
    private const int MaxDispatchAttempts = 2;

    private readonly ITaskEventSource _source;
    private readonly IActorRegistry _actors;
    private readonly ILogger<TaskLoop> _logger;
    private readonly TimeSpan _pollTimeout;
    private readonly TimeSpan _scanInterval;

    private readonly HashSet<int> _initializedPartitions = new();

    public TaskLoop(
        IOptions<TaskOptions> options,
        ITaskEventSource source,
        IActorRegistry actors,
        ILogger<TaskLoop> logger)
    {
        _source = source;
        _actors = actors;
        _logger = logger;
        _pollTimeout = TimeSpan.FromMilliseconds(options.Value.Kafka.PollTimeoutMs);
        _scanInterval = TimeSpan.FromSeconds(options.Value.Worker.ActorScanIntervalSeconds);
    }

    protected override global::System.Threading.Tasks.Task ExecuteAsync(CancellationToken stoppingToken)
        => global::System.Threading.Tasks.Task.Factory.StartNew(
            () => Run(stoppingToken),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private void Run(CancellationToken ct)
    {
        _source.OnPartitionsAssigned = OnPartitionsAssigned;
        _source.OnPartitionsRevoked = OnPartitionsRevoked;

        try
        {
            _source.Open();
            _logger.LogInformation("Task loop started.");

            var nextScan = DateTime.UtcNow + _scanInterval;
            while (!ct.IsCancellationRequested)
            {
                var message = _source.Poll(_pollTimeout);
                if (message is not null)
                {
                    HandleMessage(message, ct);
                }

                if (DateTime.UtcNow >= nextScan)
                {
                    _actors.CleanupCompletedActors();
                    nextScan = DateTime.UtcNow + _scanInterval;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // graceful shutdown
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Task loop crashed.");
            throw;
        }
        finally
        {
            // Order matters: stop actors first, then close the consumer
            // (which commits the offsets stored so far).
            _actors.CleanupAsync().GetAwaiter().GetResult();
            _source.Close();
            _logger.LogInformation("Task loop stopped.");
        }
    }

    private void HandleMessage(ConsumedMessage message, CancellationToken ct)
    {
        if (_initializedPartitions.Add(message.Partition))
        {
            // TODO(watermark): InitializePartitionOffset(partition, offset).
            _logger.LogInformation(
                "First message on partition. partition={Partition} offset={Offset}",
                message.Partition, message.Offset);
        }

        if (TryDecode(message, out var evt))
        {
            Dispatch(new TaskEventEnvelope(evt, message.Partition, message.Offset), ct);
        }

        // TEMP: at-most-once. Undecodable/undeliverable events are skipped
        // here on purpose so one bad message cannot wedge the partition.
        _source.StoreOffset(message);
    }

    private bool TryDecode(ConsumedMessage message, out TaskEvent evt)
    {
        evt = null!;
        if (message.Value is null)
        {
            _logger.LogWarning(
                "Empty message skipped. partition={Partition} offset={Offset}",
                message.Partition, message.Offset);
            return false;
        }

        try
        {
            evt = TaskEvent.Parser.ParseFrom(message.Value);
        }
        catch (InvalidProtocolBufferException ex)
        {
            _logger.LogError(ex,
                "Undecodable TaskEvent skipped. partition={Partition} offset={Offset}",
                message.Partition, message.Offset);
            return false;
        }

        if (string.IsNullOrEmpty(evt.TaskType) || string.IsNullOrEmpty(evt.TaskId))
        {
            _logger.LogWarning(
                "TaskEvent missing required fields, skipped. partition={Partition} offset={Offset} task_type={TaskType} task_id={TaskId}",
                message.Partition, message.Offset, evt.TaskType, evt.TaskId);
            return false;
        }
        return true;
    }

    private void Dispatch(TaskEventEnvelope envelope, CancellationToken ct)
    {
        var evt = envelope.Event;

        // An actor can retire between GetOrCreateActor handing it out and the
        // enqueue landing. It then reports NotRunning and the registry builds a
        // fresh one on the next pass.
        for (var attempt = 1; attempt <= MaxDispatchAttempts; attempt++)
        {
            ITaskActor actor;
            try
            {
                actor = _actors.GetOrCreateActor(evt.TaskId, evt.TaskType);
            }
            catch (KeyNotFoundException)
            {
                _logger.LogError(
                    "No task registered for task_type, event skipped. task_type={TaskType} task_id={TaskId} event_id={EventId}",
                    evt.TaskType, evt.TaskId, evt.EventId);
                return;
            }

            // Blocks this thread while the actor's mailbox is full: the
            // partition pauses rather than dropping the event.
            var result = actor.EnqueueAsync(envelope, ct).AsTask().GetAwaiter().GetResult();
            if (result == EnqueueResult.Ok)
            {
                return;
            }

            _logger.LogDebug(
                "Actor retired mid-dispatch (attempt {Attempt}), recreating. task_id={TaskId}",
                attempt, evt.TaskId);
        }

        _logger.LogError(
            "Event could not be delivered after {Attempts} attempts, skipped. task_type={TaskType} task_id={TaskId} event_id={EventId}",
            MaxDispatchAttempts, evt.TaskType, evt.TaskId, evt.EventId);
    }

    private void OnPartitionsAssigned(IReadOnlyList<int> partitions)
    {
        _logger.LogInformation("Partitions assigned: [{Partitions}]", string.Join(",", partitions));
        ForgetPartitions(partitions);
    }

    private void OnPartitionsRevoked(IReadOnlyList<int> partitions)
    {
        _logger.LogInformation("Partitions revoked: [{Partitions}]", string.Join(",", partitions));
        ForgetPartitions(partitions);
    }

    /// <summary>
    /// Partition state must not survive losing (or regaining) ownership:
    /// another consumer may have advanced it in between.
    /// </summary>
    private void ForgetPartitions(IReadOnlyList<int> partitions)
    {
        foreach (var partition in partitions)
        {
            _initializedPartitions.Remove(partition);
            // TODO(watermark): reset the partition's watermark state.
        }
    }

    internal IReadOnlyCollection<int> InitializedPartitions => _initializedPartitions;
}
