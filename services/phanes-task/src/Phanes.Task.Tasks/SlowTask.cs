using Phanes.Task.Contracts;
using Phanes.Task.Sdk;

namespace Phanes.Task.Tasks;

using System.Threading.Tasks;

/// <summary>
/// Sleeps for content milliseconds (default 5000), honouring cancellation.
/// For watching in-flight offsets hold the watermark, and for kill tests.
/// </summary>
[PhanesTask(PhanesNativeTaskTypes.Slow)]
public sealed class SlowTask : ITask
{
    public async Task<TaskResult> ExecuteAsync(TaskExecution execution, TaskEvent evt, CancellationToken ct)
    {
        var ms = int.TryParse(evt.Content, out var parsed) ? parsed : 5000;
        await Task.Delay(ms, ct);
        return TaskResults.Ok(execution, "slept");
    }

    public bool IsFinished() => true;
}
