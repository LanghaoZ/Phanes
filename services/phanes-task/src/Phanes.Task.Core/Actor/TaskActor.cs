using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Phanes.Task.Core.Registry;

namespace Phanes.Task.Core.Actor;

/// <summary>
/// Default ITaskActor implementation. Owns a bounded Channel&lt;TaskEvent&gt;
/// fed by the Task Loop and drains it sequentially, invoking the registered
/// ITask.ExecuteAsync for each event.
/// </summary>
public sealed class TaskActor : ITaskActor
{
    private readonly TaskRegistration _registration;
    private readonly Channel<TaskEvent> _channel;
    private readonly ILogger<TaskActor> _logger;
    private readonly TimeSpan _idleTimeout;

    private DateTime _lastActivityUtc;
    private bool _completed;

    public TaskActor(
        string taskId,
        TaskRegistration registration,
        int channelCapacity,
        TimeSpan idleTimeout,
        ILogger<TaskActor> logger)
    {
        TaskId = taskId;
        _registration = registration;
        _channel = Channel.CreateBounded<TaskEvent>(new BoundedChannelOptions(channelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
        _idleTimeout = idleTimeout;
        _logger = logger;
        _lastActivityUtc = DateTime.UtcNow;
    }

    public string TaskId      { get; }
    public bool   IsCompleted => _completed;
    public bool   IsIdle      => DateTime.UtcNow - _lastActivityUtc > _idleTimeout;

    public bool TryEnqueue(TaskEvent evt)
    {
        if (_channel.Writer.TryWrite(evt))
        {
            _lastActivityUtc = DateTime.UtcNow;
            return true;
        }
        return false;
    }

    public async global::System.Threading.Tasks.Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var evt in _channel.Reader.ReadAllAsync(ct))
            {
                _lastActivityUtc = DateTime.UtcNow;
                try
                {
                    var result = await _registration.Task.ExecuteAsync(evt, _registration.Context, ct);
                    if (!result.Ok)
                    {
                        _logger.LogWarning(
                            "Task execution returned not-ok. task_id={TaskId} event_id={EventId} error={Error}",
                            evt.TaskId, evt.EventId, result.ErrorMessage);
                    }
                    // TODO(watermark): NotifyCompletion(partition, offset) once WatermarkManager lands.
                    // TODO(followup): if result.FollowupEvent != null, re-publish to Kafka.
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Task execution threw. task_id={TaskId} event_id={EventId}",
                        evt.TaskId, evt.EventId);
                }

                if (_registration.Task.IsFinished())
                {
                    _completed = true;
                    _channel.Writer.TryComplete();
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
    }

    public global::System.Threading.Tasks.Task SnapshotAsync()
        => throw new NotImplementedException("Phase 3");

    public global::System.Threading.Tasks.Task RestoreAsync(string taskId)
        => throw new NotImplementedException("Phase 3");
}
