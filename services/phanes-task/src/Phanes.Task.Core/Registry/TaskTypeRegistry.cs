using System.Reflection;
using Microsoft.Extensions.Logging;
using Phanes.Task.Sdk;

namespace Phanes.Task.Core.Registry;

using System.Threading.Tasks;

/// <summary>
/// Scans assemblies for [PhanesTask] ITask classes. Instances are NOT created
/// here — the DI container builds one per actor scope. Type-level setup
/// (ITaskTypeSetup.Configure) runs once via <see cref="ConfigureSharedContexts"/>
/// after the container exists.
/// </summary>
public sealed class TaskTypeRegistry : ITaskTypeRegistry
{
    private readonly Dictionary<string, TaskTypeDescriptor> _types = new();
    private readonly Dictionary<string, TaskContext> _contexts = new();

    public TaskTypeRegistry(IEnumerable<Assembly> assemblies, ILoggerFactory loggerFactory)
    {
        foreach (var type in assemblies.SelectMany(a => a.GetTypes()))
        {
            if (type.IsAbstract || type.IsInterface || !typeof(ITask).IsAssignableFrom(type))
            {
                continue;
            }
            var attr = type.GetCustomAttribute<PhanesTaskAttribute>()
                ?? throw new InvalidOperationException(
                    $"{type.FullName} implements ITask but has no [PhanesTask(\"<owner>.<name>\")] attribute.");

            if (_types.ContainsKey(attr.TaskType))
            {
                throw new InvalidOperationException(
                    $"Duplicate task_type '{attr.TaskType}' ({type.FullName} and {_types[attr.TaskType].ClrType.FullName}).");
            }

            var logger = loggerFactory.CreateLogger($"Phanes.Task[{attr.TaskType}]");
            var context = new TaskContext(attr.TaskType, logger);
            _contexts[attr.TaskType] = context;
            _types[attr.TaskType] = new TaskTypeDescriptor(attr.TaskType, type, context, logger);
        }
    }

    public bool TryGet(string taskType, out TaskTypeDescriptor descriptor)
        => _types.TryGetValue(taskType, out descriptor!);

    public IReadOnlyCollection<TaskTypeDescriptor> All => _types.Values;

    /// <summary>
    /// Run ITaskTypeSetup.Configure for every type that implements it, then
    /// seal all contexts. `createSetupInstance` resolves a throwaway instance
    /// from the container (so constructor dependencies work).
    /// </summary>
    public void ConfigureSharedContexts(Func<TaskTypeDescriptor, ITaskTypeSetup?> createSetupInstance)
    {
        foreach (var descriptor in _types.Values)
        {
            if (typeof(ITaskTypeSetup).IsAssignableFrom(descriptor.ClrType))
            {
                var setup = createSetupInstance(descriptor)
                    ?? throw new InvalidOperationException($"Could not create setup instance for '{descriptor.TaskType}'.");
                setup.Configure(descriptor.Shared);
            }
            _contexts[descriptor.TaskType].Seal();
        }
    }
}
