using Phanes.Task.Contracts;

namespace Phanes.Task.Sdk;

using System.Threading.Tasks;

/// <summary>Convenience constructors for TaskResult.</summary>
public static class TaskResults
{
    public static TaskResult Ok(TaskExecution execution, string? completionReason = null)
        => new()
        {
            TaskId = execution.TaskId,
            EventId = execution.EventId,
            Ok = true,
            Status = "completed",
            CompletionReason = completionReason ?? string.Empty,
        };

    public static TaskResult Fail(TaskExecution execution, string errorMessage)
        => new()
        {
            TaskId = execution.TaskId,
            EventId = execution.EventId,
            Ok = false,
            Status = "failed",
            ErrorMessage = errorMessage,
        };

    /// <summary>Follow-up for the SAME task (self-chain): keeps task_type/task_id, new event_id.</summary>
    public static NextEvent Continue(TaskExecution execution, string content, TimeSpan? delay = null)
        => new()
        {
            TaskType = execution.TaskType,
            TaskId = execution.TaskId,
            EventId = Guid.NewGuid().ToString("N"),
            Content = content,
            DelayMs = (long)(delay?.TotalMilliseconds ?? 0),
        };

    /// <summary>Follow-up that spawns/addresses another task.</summary>
    public static NextEvent Spawn(string taskType, string taskId, string content, TimeSpan? delay = null)
        => new()
        {
            TaskType = taskType,
            TaskId = taskId,
            EventId = Guid.NewGuid().ToString("N"),
            Content = content,
            DelayMs = (long)(delay?.TotalMilliseconds ?? 0),
        };
}
