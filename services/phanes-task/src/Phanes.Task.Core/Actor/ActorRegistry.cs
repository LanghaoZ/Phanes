using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phanes.Task.Core.Options;
using Phanes.Task.Core.Registry;

namespace Phanes.Task.Core.Actor;

/// <summary>
/// Default IActorRegistry implementation. Holds one ITaskActor per task_id in a
/// ConcurrentDictionary. Actors are created lazily on first event arrival and
/// evicted when completed or idle by the periodic cleanup scan.
/// </summary>
public sealed class ActorRegistry : IActorRegistry
{
    private readonly ConcurrentDictionary<string, ActorEntry> _actors = new();
    private readonly ITaskRegistry _taskRegistry;
    private readonly WorkerOptions _worker;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ActorRegistry> _logger;

    public ActorRegistry(
        ITaskRegistry taskRegistry,
        IOptions<TaskOptions> options,
        ILoggerFactory loggerFactory)
    {
        _taskRegistry = taskRegistry;
        _worker = options.Value.Worker;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ActorRegistry>();
    }

    public ITaskActor GetOrCreateActor(string taskId, string taskType)
    {
        return _actors.GetOrAdd(taskId, id => CreateAndStart(id, taskType)).Actor;
    }

    public int CleanupCompletedActors()
    {
        var removed = 0;
        foreach (var (taskId, entry) in _actors)
        {
            if (entry.Actor.IsCompleted || entry.Actor.IsIdle)
            {
                if (_actors.TryRemove(taskId, out var taken))
                {
                    taken.Cts.Cancel();
                    removed++;
                }
            }
        }
        if (removed > 0)
        {
            _logger.LogDebug("Evicted {Count} actors during cleanup scan.", removed);
        }
        return removed;
    }

    public int RunningCount(string taskType)
    {
        // Walking-skeleton stub — accurate per-type count requires tracking
        // task_type on the entry; deferred until we actually need it.
        return _actors.Count;
    }

    public async global::System.Threading.Tasks.Task CleanupAsync()
    {
        foreach (var (_, entry) in _actors)
        {
            entry.Cts.Cancel();
        }
        await global::System.Threading.Tasks.Task.WhenAll(
            _actors.Values.Select(e => e.RunTask));
        _actors.Clear();
    }

    private ActorEntry CreateAndStart(string taskId, string taskType)
    {
        if (!_taskRegistry.TryResolve(taskType, out var registration))
        {
            throw new KeyNotFoundException(
                $"No ITask registered for task_type '{taskType}' (task_id '{taskId}').");
        }

        var cts = new CancellationTokenSource();
        var actor = new TaskActor(
            taskId,
            registration,
            channelCapacity: 64,
            idleTimeout: TimeSpan.FromSeconds(_worker.ActorIdleTimeoutSeconds),
            logger: _loggerFactory.CreateLogger<TaskActor>());

        var runTask = global::System.Threading.Tasks.Task.Run(() => actor.RunAsync(cts.Token));
        return new ActorEntry(actor, cts, runTask);
    }

    private sealed record ActorEntry(
        ITaskActor Actor,
        CancellationTokenSource Cts,
        global::System.Threading.Tasks.Task RunTask);
}
