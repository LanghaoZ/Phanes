using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Phanes.Task.Core.Model;
using Phanes.Task.Core.Registry;

namespace Phanes.Task.Core.Actor;

/// <summary>
/// Default ITaskActor. See ITaskActor for the lifecycle contract.
///
/// Concurrency: EnqueueAsync is called from the Task Loop thread, RunAsync
/// runs on the thread pool. The two meet only at <see cref="_gate"/>, which
/// makes "accept an event" and "retire" mutually exclusive.
/// </summary>
public sealed class TaskActor : ITaskActor
{
    private readonly TaskRegistration _registration;
    private readonly Channel<TaskEventEnvelope> _mailbox;
    private readonly TimeSpan _idleTimeout;
    private readonly ILogger<TaskActor> _logger;

    private readonly object _gate = new();
    private bool _running = true;
    private volatile bool _completed;

    public TaskActor(
        string taskId,
        string taskType,
        TaskRegistration registration,
        int mailboxCapacity,
        TimeSpan idleTimeout,
        ILogger<TaskActor> logger)
    {
        TaskId = taskId;
        TaskType = taskType;
        _registration = registration;
        _mailbox = Channel.CreateBounded<TaskEventEnvelope>(new BoundedChannelOptions(mailboxCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _idleTimeout = idleTimeout;
        _logger = logger;
    }

    public string TaskId   { get; }
    public string TaskType { get; }

    public bool IsRunning
    {
        get { lock (_gate) { return _running; } }
    }

    public bool IsCompleted => _completed;

    public async ValueTask<EnqueueResult> EnqueueAsync(TaskEventEnvelope envelope, CancellationToken ct)
    {
        while (true)
        {
            lock (_gate)
            {
                if (!_running)
                {
                    return EnqueueResult.NotRunning;
                }
                if (_mailbox.Writer.TryWrite(envelope))
                {
                    return EnqueueResult.Ok;
                }
            }

            // Mailbox full: wait outside the gate. Returns false once the
            // actor retires and completes the writer.
            if (!await _mailbox.Writer.WaitToWriteAsync(ct))
            {
                return EnqueueResult.NotRunning;
            }
        }
    }

    public async global::System.Threading.Tasks.Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                if (!await WaitForEventOrIdleAsync(ct))
                {
                    if (TryRetire("idle"))
                    {
                        return;
                    }
                    continue; // an event slipped in before we could retire
                }

                while (_mailbox.Reader.TryRead(out var envelope))
                {
                    await ProcessAsync(envelope, ct);

                    if (_registration.Task.IsFinished() && TryRetire("finished"))
                    {
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // process shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Actor run loop faulted; retiring actor. task_type={TaskType} task_id={TaskId}",
                TaskType, TaskId);
        }
        finally
        {
            Retire();
        }
    }

    /// <summary>True if an event is available, false on idle timeout.</summary>
    private async ValueTask<bool> WaitForEventOrIdleAsync(CancellationToken ct)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(_idleTimeout);
        try
        {
            return await _mailbox.Reader.WaitToReadAsync(idle.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private async global::System.Threading.Tasks.Task ProcessAsync(TaskEventEnvelope envelope, CancellationToken ct)
    {
        var evt = envelope.Event;
        try
        {
            var result = await _registration.Task.ExecuteAsync(evt, _registration.Context, ct);
            if (!result.Ok)
            {
                _logger.LogWarning(
                    "Task execution returned not-ok. task_id={TaskId} event_id={EventId} error={Error}",
                    evt.TaskId, evt.EventId, result.ErrorMessage);
            }
            // TODO(watermark): NotifyCompletion(envelope.Partition, envelope.Offset).
            // TODO(followup): persist result.FollowupEvent BEFORE reporting completion.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Task execution threw. task_id={TaskId} event_id={EventId} partition={Partition} offset={Offset}",
                evt.TaskId, evt.EventId, envelope.Partition, envelope.Offset);
        }
    }

    /// <summary>
    /// Retire only if the mailbox is empty. Mutually exclusive with
    /// EnqueueAsync: a racing enqueue either landed first (we keep running to
    /// drain it) or observes _running == false and reports NotRunning.
    /// </summary>
    private bool TryRetire(string reason)
    {
        lock (_gate)
        {
            if (_mailbox.Reader.Count > 0)
            {
                return false;
            }
            _running = false;
            _completed = true;
            _mailbox.Writer.TryComplete();
        }
        _logger.LogDebug(
            "Actor retired ({Reason}). task_type={TaskType} task_id={TaskId}", reason, TaskType, TaskId);
        return true;
    }

    /// <summary>
    /// Unconditional retirement on shutdown or fault. Events still buffered are
    /// not processed; they are logged so the loss is visible. (At-most-once
    /// today; once offsets are committed on completion they will be redelivered.)
    /// </summary>
    private void Retire()
    {
        lock (_gate)
        {
            _running = false;
            _completed = true;
            _mailbox.Writer.TryComplete();
        }

        var abandoned = 0;
        while (_mailbox.Reader.TryRead(out _))
        {
            abandoned++;
        }
        if (abandoned > 0)
        {
            _logger.LogWarning(
                "Actor retired with {Count} unprocessed events. task_type={TaskType} task_id={TaskId}",
                abandoned, TaskType, TaskId);
        }
    }

    public global::System.Threading.Tasks.Task SnapshotAsync()
        => throw new NotImplementedException("Phase 3");

    public global::System.Threading.Tasks.Task RestoreAsync(string taskId)
        => throw new NotImplementedException("Phase 3");
}
