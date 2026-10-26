using Microsoft.Extensions.Logging;

namespace Phanes.Task.Sdk;

using System.Threading.Tasks;

/// <summary>
/// Per-execution identity and lineage, passed explicitly to ExecuteAsync.
/// The framework also pushes TaskType/TaskId/EventId into the ambient log
/// context and trace activity, so task code need not repeat them in logs.
/// </summary>
public sealed record TaskExecution(
    string TaskType,
    string TaskId,
    string EventId,
    string RootTaskId,
    string RootTaskType,
    string ParentTaskId,
    int ChainDepth,
    ITaskContext Shared,
    ILogger Logger,
    IServiceProvider Services);
