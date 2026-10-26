using Microsoft.Extensions.Logging;
using Phanes.Task.Sdk;

namespace Phanes.Task.Core.Registry;

using System.Threading.Tasks;

/// <summary>
/// Default ITaskContext implementation. One instance per registered task type.
/// Mutable during Configure, sealed afterward — calls to Set after Seal throw.
/// </summary>
internal sealed class TaskContext : ITaskContext
{
    private readonly Dictionary<string, object> _resources = new();
    private bool _sealed;

    public TaskContext(string taskType, ILogger logger)
    {
        TaskType = taskType;
        Logger = logger;
    }

    public string  TaskType { get; }
    public ILogger Logger   { get; }

    public void Set<T>(string key, T value) where T : notnull
    {
        if (_sealed)
        {
            throw new InvalidOperationException(
                $"TaskContext for '{TaskType}' is sealed; Set must only be called during Configure.");
        }
        _resources[key] = value;
    }

    public T Get<T>(string key) where T : notnull
    {
        if (!_resources.TryGetValue(key, out var value))
        {
            throw new KeyNotFoundException(
                $"Resource '{key}' not registered in TaskContext for '{TaskType}'.");
        }
        return (T)value;
    }

    public void Seal() => _sealed = true;
}
