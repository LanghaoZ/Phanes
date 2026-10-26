using System.Reflection;
using Microsoft.Extensions.Logging;
using Phanes.Task.Sdk;

namespace Phanes.Task.Core.Registry;

/// <summary>
/// Default ITaskRegistry implementation. At construction time, scans the
/// provided assemblies for ITask implementations, instantiates each, creates
/// a TaskContext, invokes Configure, seals the context, and stores the pair
/// keyed on task.TaskType.
///
/// Built-in tasks live in Phanes.Task.Tasks and are picked up by the default
/// assembly list passed in by the Host.
/// </summary>
public sealed class TaskRegistry : ITaskRegistry
{
    private readonly Dictionary<string, TaskRegistration> _registrations = new();

    public TaskRegistry(IEnumerable<Assembly> assemblies, ILoggerFactory loggerFactory)
    {
        foreach (var assembly in assemblies)
        {
            ScanAndRegister(assembly, loggerFactory);
        }
    }

    public TaskRegistration Resolve(string taskType)
    {
        if (!_registrations.TryGetValue(taskType, out var registration))
        {
            throw new KeyNotFoundException($"No ITask registered for task_type '{taskType}'.");
        }
        return registration;
    }

    public bool TryResolve(string taskType, out TaskRegistration registration)
        => _registrations.TryGetValue(taskType, out registration!);

    public IReadOnlyCollection<string> RegisteredTaskTypes => _registrations.Keys;

    private void ScanAndRegister(Assembly assembly, ILoggerFactory loggerFactory)
    {
        var taskTypes = assembly.GetTypes()
            .Where(t => typeof(ITask).IsAssignableFrom(t)
                        && !t.IsInterface
                        && !t.IsAbstract);

        foreach (var type in taskTypes)
        {
            var task = (ITask)Activator.CreateInstance(type)!;
            var logger = loggerFactory.CreateLogger($"Phanes.Task[{task.TaskType}]");
            var context = new TaskContext(task.TaskType, logger);

            task.Configure(context);
            context.Seal();

            if (!_registrations.TryAdd(task.TaskType, new TaskRegistration(task, context)))
            {
                throw new InvalidOperationException(
                    $"Duplicate ITask registration for task_type '{task.TaskType}' " +
                    $"(found in {type.FullName} and {_registrations[task.TaskType].Task.GetType().FullName}).");
            }
        }
    }
}
