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
    WorkerOptions Worker);
