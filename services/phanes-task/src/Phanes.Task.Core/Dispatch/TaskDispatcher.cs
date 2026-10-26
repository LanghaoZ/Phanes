using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phanes.Task.Core.Options;

namespace Phanes.Task.Core.Dispatch;

using System.Threading.Tasks;

/// <summary>
/// Default ITaskDispatcher. Immediate submits publish directly; delayed
/// submits park on an in-process timer and publish when due. Shutdown stops
/// accepting work (DispatcherClosedException) so callers can leave their
/// source event uncommitted for redelivery instead of inventing a terminal
/// state.
/// </summary>
public sealed class TaskDispatcher : ITaskDispatcher, IAsyncDisposable
{
    private readonly ITaskEventPublisher _publisher;
    private readonly ILogger<TaskDispatcher> _logger;
    private readonly TimeSpan _maxDelay;
    private readonly CancellationTokenSource _closing = new();
    private readonly HashSet<Task> _pending = new();
    private readonly object _gate = new();

    public TaskDispatcher(
        ITaskEventPublisher publisher,
        IOptions<TaskOptions> options,
        ILogger<TaskDispatcher> logger)
    {
        _publisher = publisher;
        _logger = logger;
        _maxDelay = TimeSpan.FromSeconds(options.Value.Dispatcher.MaxDelaySeconds);
    }

    public int PendingDelayed
    {
        get { lock (_gate) { return _pending.Count; } }
    }

    public Task SubmitAsync(TaskEvent evt, CancellationToken ct)
    {
        ThrowIfClosed();
        return _publisher.PublishAsync(evt, ct);
    }

    public Task SubmitWithDelayAsync(TaskEvent evt, TimeSpan delay, CancellationToken ct)
    {
        ThrowIfClosed();
        if (delay > _maxDelay)
        {
            throw new DelayTooLongException(delay, _maxDelay);
        }
        if (delay <= TimeSpan.Zero)
        {
            return _publisher.PublishAsync(evt, ct);
        }

        // Accepted now; published later. The caller's offset is released on
        // acceptance — a restart in between loses the timer (documented).
        Task pending = null!;
        pending = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, _closing.Token);
                await _publisher.PublishAsync(evt, _closing.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "Delayed event dropped at shutdown. task_type={TaskType} task_id={TaskId} event_id={EventId}",
                    evt.TaskType, evt.TaskId, evt.EventId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Delayed event publish failed. task_type={TaskType} task_id={TaskId} event_id={EventId}",
                    evt.TaskType, evt.TaskId, evt.EventId);
            }
            finally
            {
                lock (_gate) { _pending.Remove(pending); }
            }
        });
        lock (_gate) { _pending.Add(pending); }
        return Task.CompletedTask;
    }

    public Task KillAsync(string taskType, string taskId, CancellationToken ct)
    {
        ThrowIfClosed();
        return _publisher.PublishAsync(new TaskEvent
        {
            TaskType = taskType,
            TaskId = taskId,
            EventId = Guid.NewGuid().ToString("N"),
            Kind = TaskEventKind.Kill,
        }, ct);
    }

    private void ThrowIfClosed()
    {
        if (_closing.IsCancellationRequested)
        {
            throw new DispatcherClosedException();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _closing.Cancel();
        Task[] pending;
        lock (_gate) { pending = _pending.ToArray(); }
        await Task.WhenAll(pending);
    }
}
