using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phanes.Task.Core.Dispatch;
using Phanes.Task.Core.Options;
using Phanes.Task.Core.Registry;
using Phanes.Task.Core.Status;
using Phanes.Task.Core.Watermark;

namespace Phanes.Task.Core.Actor;

using System.Threading.Tasks;

/// <summary>
/// Default IActorRegistry. Plain dictionaries, no locks — see the threading
/// note on IActorRegistry. Each actor gets its own DI scope from
/// <see cref="IActorScopeFactory"/>; the task instance lives in that scope.
/// </summary>
public sealed class ActorRegistry : IActorRegistry
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, Dictionary<string, ActorEntry>> _actors = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ITaskTypeRegistry _types;
    private readonly IActorScopeFactory _scopes;
    private readonly ActorServices _services;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ActorRegistry> _logger;

    public ActorRegistry(
        ITaskTypeRegistry types,
        IActorScopeFactory scopes,
        IExecutionStatusStore status,
        ITaskDispatcher dispatcher,
        IWatermarkManager watermark,
        IOptions<TaskOptions> options,
        ILoggerFactory loggerFactory)
    {
        _types = types;
        _scopes = scopes;
        _services = new ActorServices(status, dispatcher, watermark, options.Value.Worker);
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

    public ITaskActor? TryGetRunning(string taskId, string taskType)
        => _actors.TryGetValue(taskType, out var byId)
           && byId.TryGetValue(taskId, out var entry)
           && entry.Actor.IsRunning
            ? entry.Actor
            : null;

    public int CleanupCompletedActors()
    {
        var removed = 0;
        foreach (var (taskType, byId) in _actors.ToArray())
        {
            foreach (var taskId in byId.Where(kv => kv.Value.Actor.IsCompleted).Select(kv => kv.Key).ToArray())
            {
                byId.Remove(taskId);
                removed++;
            }
            if (byId.Count == 0)
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
        => _actors.TryGetValue(taskType, out var byId) ? byId.Values.Count(e => e.Actor.IsRunning) : 0;

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
        if (!_types.TryGet(taskType, out var descriptor))
        {
            throw new KeyNotFoundException(
                $"No ITask registered for task_type '{taskType}' (task_id '{taskId}').");
        }

        var scope = _scopes.Create(taskType, taskId);
        var actor = new TaskActor(taskId, descriptor, scope, _services, _loggerFactory.CreateLogger<TaskActor>());
        var runTask = global::System.Threading.Tasks.Task.Run(() => actor.RunAsync(_shutdown.Token));
        return new ActorEntry(actor, runTask);
    }

    private sealed record ActorEntry(ITaskActor Actor, global::System.Threading.Tasks.Task RunTask);
}
