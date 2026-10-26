using Microsoft.Extensions.Logging;
using Phanes.Task.Contracts;
using Phanes.Task.Sdk;

namespace Phanes.Task.Tasks;

/// <summary>
/// Minimal walking-skeleton task that just logs the event it received.
/// Used to validate the end-to-end Kafka → TaskLoop → ActorRegistry → TaskActor → ITask path.
/// </summary>
public sealed class DummyTask : ITask
{
    public string TaskType => PhanesNativeTaskTypes.Dummy;

    public void Configure(ITaskContext ctx)
    {
        // No shared resources to register.
    }

    public global::System.Threading.Tasks.Task<TaskResult> ExecuteAsync(
        TaskEvent evt, ITaskContext ctx, CancellationToken ct = default)
    {
        ctx.Logger.LogInformation(
            "DummyTask executed. task_id={TaskId} event_id={EventId} content={Content} context_count={ContextCount}",
            evt.TaskId, evt.EventId, evt.Content, evt.Context.Count);

        return global::System.Threading.Tasks.Task.FromResult(new TaskResult
        {
            TaskId   = evt.TaskId,
            EventId  = evt.EventId,
            Ok       = true,
            Status   = "done",
        });
    }

    public bool IsFinished() => true;
}
