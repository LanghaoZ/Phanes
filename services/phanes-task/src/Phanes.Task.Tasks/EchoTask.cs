using Microsoft.Extensions.Logging;
using Phanes.Task.Contracts;
using Phanes.Task.Sdk;

namespace Phanes.Task.Tasks;

using System.Threading.Tasks;

/// <summary>Logs the event and completes immediately. The smoke-test task.</summary>
[PhanesTask(PhanesNativeTaskTypes.Echo)]
public sealed class EchoTask : ITask
{
    private bool _done;

    public Task<TaskResult> ExecuteAsync(TaskExecution execution, TaskEvent evt, CancellationToken ct)
    {
        execution.Logger.LogInformation("Echo: {Content}", evt.Content);
        _done = true;
        return Task.FromResult(TaskResults.Ok(execution, "echoed"));
    }

    public bool IsFinished() => _done;
}
