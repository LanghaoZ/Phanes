using Phanes.Task.Contracts;
using Phanes.Task.Sdk;

namespace Phanes.Task.Tasks;

using System.Threading.Tasks;

/// <summary>
/// Fails on command, for exercising failure paths: content "fail" → ok=false,
/// "throw" → exception, anything else → success.
/// </summary>
[PhanesTask(PhanesNativeTaskTypes.Flaky)]
public sealed class FlakyTask : ITask
{
    public Task<TaskResult> ExecuteAsync(TaskExecution execution, TaskEvent evt, CancellationToken ct)
        => evt.Content switch
        {
            "fail" => Task.FromResult(TaskResults.Fail(execution, "asked to fail")),
            "throw" => throw new InvalidOperationException("asked to throw"),
            _ => Task.FromResult(TaskResults.Ok(execution)),
        };

    public bool IsFinished() => true;
}
