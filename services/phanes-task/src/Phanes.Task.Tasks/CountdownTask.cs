using Microsoft.Extensions.Logging;
using Phanes.Task.Contracts;
using Phanes.Task.Sdk;

namespace Phanes.Task.Tasks;

using System.Threading.Tasks;

/// <summary>
/// Multi-step task driven by self-chained follow-ups. Content is the
/// remaining count; each step emits Continue(n-1) until it reaches 0.
/// Context "step_delay_ms" (optional) delays each hop. Exercises the
/// continuation path, per-task instance state and the delayed dispatcher.
/// </summary>
[PhanesTask(PhanesNativeTaskTypes.Countdown)]
public sealed class CountdownTask : ITask
{
    private int _stepsRun;
    private bool _done;

    public Task<TaskResult> ExecuteAsync(TaskExecution execution, TaskEvent evt, CancellationToken ct)
    {
        if (!int.TryParse(evt.Content, out var remaining) || remaining < 0)
        {
            return Task.FromResult(TaskResults.Fail(execution, $"content must be a non-negative integer, got '{evt.Content}'"));
        }

        _stepsRun++;
        execution.Logger.LogInformation("Countdown {Remaining} (steps run by this instance: {Steps})", remaining, _stepsRun);

        if (remaining == 0)
        {
            _done = true;
            return Task.FromResult(TaskResults.Ok(execution, "reached_zero"));
        }

        var delay = evt.Context.TryGetValue("step_delay_ms", out var raw) && int.TryParse(raw, out var ms)
            ? TimeSpan.FromMilliseconds(ms)
            : TimeSpan.Zero;
        var result = TaskResults.Ok(execution, "stepped");
        var next = TaskResults.Continue(execution, (remaining - 1).ToString(), delay);
        next.Context.Add(evt.Context);
        result.NextEvents.Add(next);
        return Task.FromResult(result);
    }

    public bool IsFinished() => _done;
}
