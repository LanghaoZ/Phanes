namespace Phanes.Task.Core.Options;

using System.Threading.Tasks;

public sealed class DispatcherOptions
{
    /// <summary>
    /// Upper bound for NextEvent.delay_ms. The delayed dispatcher is
    /// in-process and volatile: a restart loses pending timers, so long
    /// delays must not be routed through it.
    /// </summary>
    public int MaxDelaySeconds { get; init; } = 3600;
}
