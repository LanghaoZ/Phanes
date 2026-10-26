using Microsoft.Extensions.Logging;
using Phanes.Task.Sdk;

namespace Phanes.Task.Core.Registry;

using System.Threading.Tasks;

/// <summary>Everything the framework knows about one task_type.</summary>
public sealed record TaskTypeDescriptor(string TaskType, Type ClrType, ITaskContext Shared, ILogger Logger);

/// <summary>Task types discovered at startup (built-in assemblies now, plugins later).</summary>
public interface ITaskTypeRegistry
{
    bool TryGet(string taskType, out TaskTypeDescriptor descriptor);
    IReadOnlyCollection<TaskTypeDescriptor> All { get; }
}
