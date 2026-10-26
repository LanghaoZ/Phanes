using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Phanes.Task.Core.Dispatch;
using Phanes.Task.Core.Model;
using Phanes.Task.Core.Registry;
using Phanes.Task.Core.Status;
using Phanes.Task.Sdk;

namespace Phanes.Task.Core.Actor;

using System.Threading.Tasks;

/// <summary>
/// Default ITaskActor. See ITaskActor for the lifecycle contract.
///
/// Concurrency: EnqueueAsync is called from the Task Loop thread, RunAsync
/// runs on the thread pool. The two meet only at <see cref="_gate"/>, which
/// makes "accept an event" and "retire" mutually exclusive.
///
/// Per-event flow (mirrors the production reference):
///   Processing (CAS; already terminal → skip duplicate)
///   → in-flight registered with the watermark
///   → ExecuteAsync inside an execution scope
///   → on success: publish follow-ups FIRST, then terminal status
///   → release the offset (completion notification)
/// Shutdown mid-execution leaves the event non-terminal and unreleased so
/// Kafka redelivers it; a kill records Killed and releases.
/// </summary>
public sealed class TaskActor : ITaskActor
{
    private const int ContinuationAttempts = 3;
    private static readonly TimeSpan ContinuationBackoff = TimeSpan.FromMilliseconds(200);
    private static readonly ActivitySource Tracing = new("Phanes.Task");

    private readonly IActorScope _scope;
    private readonly TaskTypeDescriptor _type;
    private readonly ActorServices _services;
    private readonly Channel<TaskEventEnvelope> _mailbox;
    private readonly TimeSpan _idleTimeout;
    private readonly ILogger<TaskActor> _logger;

    private readonly object _gate = new();
    private readonly CancellationTokenSource _kill = new();
    private bool _running = true;
    private volatile bool _completed;

    public TaskActor(
        string taskId,
        TaskTypeDescriptor type,
        IActorScope scope,
        ActorServices services,
        ILogger<TaskActor> logger)
    {
        TaskId = taskId;
        _type = type;
        _scope = scope;
        _services = services;
        _logger = logger;
        _idleTimeout = TimeSpan.FromSeconds(services.Worker.ActorIdleTimeoutSeconds);
        _mailbox = Channel.CreateBounded<TaskEventEnvelope>(new BoundedChannelOptions(services.Worker.ActorMailboxCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public string TaskId   { get; }
    public string TaskType => _type.TaskType;

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
            if (!await _mailbox.Writer.WaitToWriteAsync(ct))
            {
                return EnqueueResult.NotRunning;
            }
        }
    }

    public void RequestKill() => _kill.Cancel();

    public async global::System.Threading.Tasks.Task RunAsync(CancellationToken shutdown)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(shutdown, _kill.Token);
        var ct = linked.Token;
        using var actorLog = _logger.BeginScope(new Dictionary<string, object>
        {
            ["TaskType"] = TaskType,
            ["TaskId"] = TaskId,
        });

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
                    continue;
                }

                while (_mailbox.Reader.TryRead(out var envelope))
                {
                    await ProcessAsync(envelope, ct);

                    if (IsTaskFinished() && TryRetire("finished"))
                    {
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutdown or kill — handled in finally
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Actor run loop faulted; retiring actor.");
        }
        finally
        {
            await RetireAsync(killed: _kill.IsCancellationRequested);
        }
    }

    private bool IsTaskFinished()
    {
        try
        {
            return _scope.Task.IsFinished();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IsFinished threw; treating the task as finished.");
            return true;
        }
    }

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

    // ---------------------------------------------------------------- events

    private async global::System.Threading.Tasks.Task ProcessAsync(TaskEventEnvelope envelope, CancellationToken ct)
    {
        var evt = envelope.Event;
        using var eventLog = _logger.BeginScope(new Dictionary<string, object>
        {
            ["EventId"] = evt.EventId,
            ["Partition"] = envelope.Partition,
            ["Offset"] = envelope.Offset,
        });

        var claim = await _services.Status.TrySetAsync(TaskType, TaskId, evt.EventId, ExecutionStatus.Processing, ct: ct);
        if (claim == StatusWriteResult.AlreadyTerminal)
        {
            _logger.LogInformation("Event already terminal; skipping duplicate delivery.");
            Release(envelope);
            return;
        }

        _services.Watermark.NotifyInFlight(envelope.Partition, envelope.Offset);

        using var activity = Tracing.StartActivity("task.execute");
        activity?.SetTag("phanes.task_type", TaskType);
        activity?.SetTag("phanes.task_id", TaskId);
        activity?.SetTag("phanes.event_id", evt.EventId);

        TaskResult result;
        try
        {
            using var execution = _scope.BeginExecution();
            var context = new TaskExecution(
                TaskType, TaskId, evt.EventId,
                RootTaskId: string.IsNullOrEmpty(evt.RootTaskId) ? TaskId : evt.RootTaskId,
                RootTaskType: string.IsNullOrEmpty(evt.RootTaskType) ? TaskType : evt.RootTaskType,
                ParentTaskId: evt.ParentTaskId,
                ChainDepth: evt.ChainDepth,
                Shared: _type.Shared,
                Logger: _type.Logger,
                Services: execution.Services);

            result = await _scope.Task.ExecuteAsync(context, evt, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (_kill.IsCancellationRequested)
            {
                await WriteTerminalAsync(evt, ExecutionStatus.Killed, "killed during execution");
                Release(envelope);
            }
            // Shutdown: leave non-terminal and unreleased → Kafka redelivers.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Task execution threw.");
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            await WriteTerminalAsync(evt, ExecutionStatus.Failed, $"{ex.GetType().Name}: {ex.Message}");
            Release(envelope);
            return;
        }

        if (!result.Ok)
        {
            _logger.LogWarning("Task returned not-ok. error={Error}", result.ErrorMessage);
            activity?.SetStatus(ActivityStatusCode.Error, "not_ok");
            await WriteTerminalAsync(evt, ExecutionStatus.Failed,
                string.IsNullOrEmpty(result.ErrorMessage) ? "task execution failed" : result.ErrorMessage);
            Release(envelope);
            return;
        }

        // Follow-ups BEFORE the terminal status and offset release: the
        // continuation must be durable before the source is marked done.
        var handoff = await PublishFollowUpsAsync(evt, result, ct);
        switch (handoff)
        {
            case Handoff.LostToShutdown:
                _logger.LogWarning(
                    "Could not publish {Count} follow-up(s) during shutdown; leaving event for redelivery.",
                    result.NextEvents.Count);
                return;
            case Handoff.Failed:
                await WriteTerminalAsync(evt, ExecutionStatus.Failed, "continuation_persist_failed");
                Release(envelope);
                return;
        }

        await WriteTerminalAsync(evt, ExecutionStatus.Completed, null,
            string.IsNullOrEmpty(result.CompletionReason) ? null : result.CompletionReason);
        Release(envelope);
    }

    private enum Handoff { Ok, Failed, LostToShutdown }

    private async global::System.Threading.Tasks.Task<Handoff> PublishFollowUpsAsync(TaskEvent source, TaskResult result, CancellationToken ct)
    {
        if (result.NextEvents.Count == 0)
        {
            return Handoff.Ok;
        }

        if (await _services.Status.IsKilledAsync(TaskType, TaskId, ct))
        {
            _logger.LogInformation("Task is killed; dropping {Count} follow-up(s) at source.", result.NextEvents.Count);
            return Handoff.Ok;
        }

        var outcome = Handoff.Ok;
        foreach (var next in result.NextEvents)
        {
            var child = BuildChild(source, next);
            if (child.ChainDepth > _services.Worker.MaxChainDepth)
            {
                _logger.LogError(
                    "Dropping follow-up: chain depth {Depth} exceeds max {Max}. next_task_type={NextType} next_task_id={NextId}",
                    child.ChainDepth, _services.Worker.MaxChainDepth, child.TaskType, child.TaskId);
                continue; // deliberate drop, not a persistence failure
            }

            var delay = TimeSpan.FromMilliseconds(next.DelayMs);
            try
            {
                await RetryAsync(ct, attempt => delay > TimeSpan.Zero
                    ? _services.Dispatcher.SubmitWithDelayAsync(child, delay, ct)
                    : _services.Dispatcher.SubmitAsync(child, ct));
            }
            catch (DelayTooLongException ex)
            {
                _logger.LogError(ex, "Dropping malformed follow-up. next_task_id={NextId}", child.TaskId);
            }
            catch (Exception ex) when (ex is DispatcherClosedException or OperationCanceledException || ct.IsCancellationRequested)
            {
                return Handoff.LostToShutdown;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Follow-up publish failed after {Attempts} attempts. next_task_id={NextId}",
                    ContinuationAttempts, child.TaskId);
                outcome = Handoff.Failed; // keep going so siblings are not stranded
            }
        }
        return outcome;
    }

    private TaskEvent BuildChild(TaskEvent source, NextEvent next)
    {
        var selfChain = next.TaskId == TaskId && next.TaskType == TaskType;
        var child = new TaskEvent
        {
            TaskType = next.TaskType,
            TaskId = next.TaskId,
            EventId = string.IsNullOrEmpty(next.EventId) ? Guid.NewGuid().ToString("N") : next.EventId,
            Content = next.Content,
            Kind = TaskEventKind.Work,
            ProducerTaskId = TaskId,
            ChainDepth = selfChain ? source.ChainDepth : source.ChainDepth + 1,
            RootTaskId = string.IsNullOrEmpty(source.RootTaskId) ? TaskId : source.RootTaskId,
            RootTaskType = string.IsNullOrEmpty(source.RootTaskType) ? TaskType : source.RootTaskType,
            ParentTaskId = TaskId,
        };
        child.Context.Add(next.Context);
        return child;
    }

    private static async global::System.Threading.Tasks.Task RetryAsync(CancellationToken ct, Func<int, global::System.Threading.Tasks.Task> attempt)
    {
        for (var i = 1; ; i++)
        {
            try
            {
                await attempt(i);
                return;
            }
            catch (Exception ex) when (
                i < ContinuationAttempts
                && ex is not (DispatcherClosedException or DelayTooLongException or OperationCanceledException)
                && !ct.IsCancellationRequested)
            {
                await global::System.Threading.Tasks.Task.Delay(ContinuationBackoff * i, ct);
            }
        }
    }

    private async global::System.Threading.Tasks.Task WriteTerminalAsync(TaskEvent evt, ExecutionStatus status, string? error, string? reason = null)
    {
        try
        {
            if (status != ExecutionStatus.Killed
                && await _services.Status.IsKilledAsync(TaskType, TaskId, CancellationToken.None))
            {
                status = ExecutionStatus.Killed;
                error = "task killed";
            }
            await _services.Status.TrySetAsync(TaskType, TaskId, evt.EventId, status, error, reason, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record terminal status {Status}.", status);
        }
    }

    private void Release(TaskEventEnvelope envelope)
        => _services.Watermark.NotifyCompletion(envelope.Partition, envelope.Offset);

    // ------------------------------------------------------------ retirement

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
        _logger.LogDebug("Actor retired ({Reason}).", reason);
        return true;
    }

    /// <summary>
    /// Unconditional retirement. Killed: buffered events are recorded Killed
    /// and released. Shutdown/fault: they stay unreleased so Kafka redelivers
    /// them (Phase 1: once the watermark commits only completed offsets).
    /// </summary>
    private async global::System.Threading.Tasks.Task RetireAsync(bool killed)
    {
        lock (_gate)
        {
            _running = false;
            _completed = true;
            _mailbox.Writer.TryComplete();
        }

        var abandoned = 0;
        while (_mailbox.Reader.TryRead(out var envelope))
        {
            abandoned++;
            if (killed)
            {
                await WriteTerminalAsync(envelope.Event, ExecutionStatus.Killed, "task killed before execution");
                Release(envelope);
            }
        }
        if (abandoned > 0)
        {
            _logger.LogWarning("Actor retired with {Count} unprocessed events (killed={Killed}).", abandoned, killed);
        }

        try
        {
            _scope.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Actor scope dispose failed.");
        }
    }

    public global::System.Threading.Tasks.Task SnapshotAsync()
        => throw new NotImplementedException("Phase 3");

    public global::System.Threading.Tasks.Task RestoreAsync(string taskId)
        => throw new NotImplementedException("Phase 3");
}
