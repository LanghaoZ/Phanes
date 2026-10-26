using Microsoft.Extensions.Options;
using Phanes.Task.Core.Dispatch;
using Phanes.Task.Core.Options;
using Phanes.Task.Core.Status;
using Phanes.Task.Core.Watermark;

namespace Phanes.Task.Core.Actor;

using System.Threading.Tasks;

/// <summary>Framework singletons every actor needs.</summary>
public sealed record ActorServices(
    IExecutionStatusStore Status,
    ITaskDispatcher Dispatcher,
    IWatermarkManager Watermark,
    IOptionsMonitor<TaskOptions> Options)
{
    /// <summary>Current worker options — re-read at each use so hot reloads apply.</summary>
    public WorkerOptions Worker => Options.CurrentValue.Worker;
}
