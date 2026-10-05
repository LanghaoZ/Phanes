using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phanes.Task.Core.Options;
using Phanes.Task.Core.Registry;

namespace Phanes.Task.Core.Actor;

/// <summary>
/// Default IActorRegistry. Plain dictionaries, no locks — see the threading
/// note on IActorRegistry.
/// </summary>
public sealed class ActorRegistry : IActorRegistry
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, Dictionary<string, ActorEntry>> _actors = new();
    private readonly CancellationTokenSource _shutdown = new();
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
        if (!_actors.TryGetValue(taskType, out var byId))
        {
            byId = new Dictionary<string, ActorEntry>();
            _actors[taskType] = byId;
        }

        if (byId.TryGetValue(taskId, out var existing))
        {
            if (existing.Actor.IsRunning)
            {
                return existing.Actor;
            }
            byId.Remove(taskId); // retired: replace it
        }

        var entry = CreateAndStart(taskId, taskType);
        byId[taskId] = entry;
        return entry.Actor;
    }

    public int CleanupCompletedActors()
    {
        var removed = 0;
        List<string>? emptyTypes = null;

        foreach (var (taskType, byId) in _actors)
        {
            List<string>? retired = null;
            foreach (var (taskId, entry) in byId)
            {
                if (entry.Actor.IsCompleted)
                {
                    (retired ??= new List<string>()).Add(taskId);
                }
            }
            if (retired is not null)
            {
                foreach (var taskId in retired)
                {
                    byId.Remove(taskId);
                }
                removed += retired.Count;
            }
            if (byId.Count == 0)
            {
                (emptyTypes ??= new List<string>()).Add(taskType);
            }
        }

        if (emptyTypes is not null)
        {
            foreach (var taskType in emptyTypes)
            {
                _actors.Remove(taskType);
            }
        }

        if (removed > 0)
        {
            _logger.LogDebug("Removed {Count} retired actors during cleanup scan.", removed);
        }
        return removed;
    }

    public int RunningCount(string taskType)
    {
        if (!_actors.TryGetValue(taskType, out var byId))
        {
            return 0;
        }
        var count = 0;
        foreach (var entry in byId.Values)
        {
            if (entry.Actor.IsRunning)
            {
                count++;
            }
        }
        return count;
    }

    public async global::System.Threading.Tasks.Task CleanupAsync()
    {
        _shutdown.Cancel();

        var runTasks = _actors.Values.SelectMany(byId => byId.Values).Select(e => e.RunTask).ToArray();
        _actors.Clear();
        if (runTasks.Length == 0)
        {
            return;
        }

        var all = global::System.Threading.Tasks.Task.WhenAll(runTasks);
        if (await global::System.Threading.Tasks.Task.WhenAny(all, global::System.Threading.Tasks.Task.Delay(ShutdownTimeout)) != all)
        {
            _logger.LogWarning(
                "{Count} actors did not stop within {Timeout}s; abandoning them.",
                runTasks.Count(t => !t.IsCompleted), ShutdownTimeout.TotalSeconds);
        }
    }

    private ActorEntry CreateAndStart(string taskId, string taskType)
    {
        if (!_taskRegistry.TryResolve(taskType, out var registration))
        {
            throw new KeyNotFoundException(
                $"No ITask registered for task_type '{taskType}' (task_id '{taskId}').");
        }

        var actor = new TaskActor(
            taskId,
            taskType,
            registration,
            mailboxCapacity: _worker.ActorMailboxCapacity,
            idleTimeout: TimeSpan.FromSeconds(_worker.ActorIdleTimeoutSeconds),
            logger: _loggerFactory.CreateLogger<TaskActor>());

        var runTask = global::System.Threading.Tasks.Task.Run(() => actor.RunAsync(_shutdown.Token));
        return new ActorEntry(actor, runTask);
    }

    private sealed record ActorEntry(ITaskActor Actor, global::System.Threading.Tasks.Task RunTask);
}
