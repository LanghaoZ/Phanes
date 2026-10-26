using Google.Protobuf;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phanes.Task.Core.Actor;
using Phanes.Task.Core.Model;
using Phanes.Task.Core.Options;
using Phanes.Task.Core.Status;
using Phanes.Task.Core.Watermark;

namespace Phanes.Task.Core.Consumer;

using System.Threading.Tasks;

/// <summary>
/// The Task Loop: one dedicated thread that does one thing per iteration —
/// poll an event and dispatch it, or run housekeeping (actor cleanup scan,
/// watermark advance + offset store). Rebalance callbacks fire on this same
/// thread (from inside Poll). Everything the loop owns — actor registry,
/// watermark heaps, partition bookkeeping — is touched only here and needs
/// no locking.
///
/// Per-message entry order (control plane first, work last):
///   decode → partition init → KILL intercept → killed-producer drop
///   → replay guard (already terminal) → Received → dispatch.
/// Every path that does not hand the event to an actor releases its offset
/// to the watermark, so a handled-but-skipped event never stalls the partition.
///
/// Offsets are committed by the watermark: only the contiguous prefix of
/// completed offsets is stored → at-least-once.
/// </summary>
public sealed class TaskLoop : BackgroundService
{
    private const int MaxDispatchAttempts = 2;

    private readonly ITaskEventSource _source;
    private readonly IActorRegistry _actors;
    private readonly IWatermarkManager _watermark;
    private readonly IExecutionStatusStore _status;
    private readonly ILogger<TaskLoop> _logger;
    private readonly TimeSpan _pollTimeout;
    private readonly TimeSpan _scanInterval;
    private readonly TimeSpan _commitInterval;

    private readonly HashSet<int> _initializedPartitions = new();

    public TaskLoop(
        IOptions<TaskOptions> options,
        ITaskEventSource source,
        IActorRegistry actors,
        IWatermarkManager watermark,
        IExecutionStatusStore status,
        ILogger<TaskLoop> logger)
    {
        _source = source;
        _actors = actors;
        _watermark = watermark;
        _status = status;
        _logger = logger;
        _pollTimeout = TimeSpan.FromMilliseconds(options.Value.Kafka.PollTimeoutMs);
        _scanInterval = TimeSpan.FromSeconds(options.Value.Worker.ActorScanIntervalSeconds);
        _commitInterval = TimeSpan.FromSeconds(options.Value.Worker.WatermarkCommitIntervalSeconds);
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
            var nextCommit = DateTime.UtcNow + _commitInterval;
            while (!ct.IsCancellationRequested)
            {
                var message = _source.Poll(_pollTimeout);
                if (message is not null)
                {
                    HandleMessage(message, ct);
                }

                var now = DateTime.UtcNow;
                if (now >= nextCommit)
                {
                    CommitWatermark(now);
                    nextCommit = now + _commitInterval;
                }
                if (now >= nextScan)
                {
                    _actors.CleanupCompletedActors();
                    nextScan = now + _scanInterval;
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
            // Stop actors, store whatever the watermark can prove complete,
            // then close the consumer (which commits the stored positions).
            _actors.CleanupAsync().GetAwaiter().GetResult();
            CommitWatermark(DateTime.UtcNow);
            _source.Close();
            _logger.LogInformation("Task loop stopped.");
        }
    }

    private void CommitWatermark(DateTime utcNow)
    {
        foreach (var (partition, nextOffset) in _watermark.Advance(utcNow))
        {
            _source.StorePosition(partition, nextOffset);
        }
    }

    // --------------------------------------------------------------- messages

    private void HandleMessage(ConsumedMessage message, CancellationToken ct)
    {
        if (_initializedPartitions.Add(message.Partition))
        {
            _watermark.InitializePartition(message.Partition, message.Offset);
        }

        if (!TryDecode(message, out var evt))
        {
            _watermark.NotifyCompletion(message.Partition, message.Offset);
            return;
        }

        if (evt.Kind == TaskEventKind.Kill)
        {
            HandleKill(evt);
            _watermark.NotifyCompletion(message.Partition, message.Offset);
            return;
        }

        if (IsKilled(evt.TaskType, evt.TaskId, ct))
        {
            _logger.LogInformation(
                "Event dropped: task is killed. task_type={TaskType} task_id={TaskId} event_id={EventId}",
                evt.TaskType, evt.TaskId, evt.EventId);
            Skip(message, evt, ExecutionStatus.Killed, "task killed", ct);
            return;
        }
        if (!string.IsNullOrEmpty(evt.ProducerTaskId) && IsKilled(evt.TaskType, evt.ProducerTaskId, ct))
        {
            _logger.LogInformation(
                "Event dropped: producer task is killed. task_type={TaskType} task_id={TaskId} producer={Producer} event_id={EventId}",
                evt.TaskType, evt.TaskId, evt.ProducerTaskId, evt.EventId);
            Skip(message, evt, ExecutionStatus.Killed, "producer task killed", ct);
            return;
        }

        if (Await(_status.IsCompletedAsync(evt.TaskType, evt.TaskId, evt.EventId, ct)))
        {
            _logger.LogInformation(
                "Event already completed (replay); skipping. task_type={TaskType} task_id={TaskId} event_id={EventId}",
                evt.TaskType, evt.TaskId, evt.EventId);
            _watermark.NotifyCompletion(message.Partition, message.Offset);
            return;
        }

        Await(_status.TrySetAsync(evt.TaskType, evt.TaskId, evt.EventId, ExecutionStatus.Received, ct: ct));

        Dispatch(new TaskEventEnvelope(evt, message.Partition, message.Offset), ct);
    }

    private void HandleKill(TaskEvent kill)
    {
        _logger.LogInformation(
            "Kill received. task_type={TaskType} task_id={TaskId} kill_event_id={EventId}",
            kill.TaskType, kill.TaskId, kill.EventId);
        try
        {
            Await(_status.MarkKilledAsync(kill.TaskType, kill.TaskId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Kill: failed to persist kill marker. task_id={TaskId}", kill.TaskId);
        }
        _actors.TryGetRunning(kill.TaskId, kill.TaskType)?.RequestKill();
    }

    private bool IsKilled(string taskType, string taskId, CancellationToken ct)
    {
        try
        {
            return Await(_status.IsKilledAsync(taskType, taskId, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Kill check failed; proceeding as not killed.");
            return false;
        }
    }

    private void Skip(ConsumedMessage message, TaskEvent evt, ExecutionStatus status, string reason, CancellationToken ct)
    {
        try
        {
            Await(_status.TrySetAsync(evt.TaskType, evt.TaskId, evt.EventId, status, reason, ct: ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to record skip status.");
        }
        _watermark.NotifyCompletion(message.Partition, message.Offset);
    }

    private bool TryDecode(ConsumedMessage message, out TaskEvent evt)
    {
        evt = null!;
        if (message.Value is null)
        {
            _logger.LogWarning("Empty message skipped. partition={Partition} offset={Offset}", message.Partition, message.Offset);
            return false;
        }
        try
        {
            evt = TaskEvent.Parser.ParseFrom(message.Value);
        }
        catch (InvalidProtocolBufferException ex)
        {
            _logger.LogError(ex, "Undecodable TaskEvent skipped. partition={Partition} offset={Offset}", message.Partition, message.Offset);
            return false;
        }
        if (string.IsNullOrEmpty(evt.TaskType) || string.IsNullOrEmpty(evt.TaskId) || string.IsNullOrEmpty(evt.EventId))
        {
            _logger.LogWarning(
                "TaskEvent missing required fields, skipped. partition={Partition} offset={Offset} task_type={TaskType} task_id={TaskId} event_id={EventId}",
                message.Partition, message.Offset, evt.TaskType, evt.TaskId, evt.EventId);
            return false;
        }
        return true;
    }

    private void Dispatch(TaskEventEnvelope envelope, CancellationToken ct)
    {
        var evt = envelope.Event;

        // An actor can retire between GetOrCreateActor handing it out and the
        // enqueue landing; it reports NotRunning and the registry builds a fresh
        // one on the next pass.
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
                Skip(new ConsumedMessage(null, envelope.Partition, envelope.Offset), evt, ExecutionStatus.Failed,
                    "no task registered for task_type", ct);
                return;
            }

            // Blocks this thread while the actor's mailbox is full: the
            // partition pauses rather than dropping the event.
            if (Await(actor.EnqueueAsync(envelope, ct).AsTask()) == EnqueueResult.Ok)
            {
                return;
            }
            _logger.LogDebug("Actor retired mid-dispatch (attempt {Attempt}), recreating. task_id={TaskId}", attempt, evt.TaskId);
        }

        _logger.LogError(
            "Event could not be delivered after {Attempts} attempts, skipped. task_type={TaskType} task_id={TaskId} event_id={EventId}",
            MaxDispatchAttempts, evt.TaskType, evt.TaskId, evt.EventId);
        Skip(new ConsumedMessage(null, envelope.Partition, envelope.Offset), evt, ExecutionStatus.Failed,
            "undeliverable", ct);
    }

    // -------------------------------------------------------------- rebalance

    private void OnPartitionsAssigned(IReadOnlyList<int> partitions)
    {
        _logger.LogInformation("Partitions assigned: [{Partitions}]", string.Join(",", partitions));
        ForgetPartitions(partitions);
    }

    private void OnPartitionsRevoked(IReadOnlyList<int> partitions)
    {
        _logger.LogInformation("Partitions revoked: [{Partitions}]", string.Join(",", partitions));
        CommitWatermark(DateTime.UtcNow); // store what we can before letting go
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
            _watermark.ForgetPartition(partition);
        }
    }

    private static T Await<T>(global::System.Threading.Tasks.Task<T> task) => task.GetAwaiter().GetResult();
    private static void Await(global::System.Threading.Tasks.Task task) => task.GetAwaiter().GetResult();

    internal IReadOnlyCollection<int> InitializedPartitions => _initializedPartitions;
}
