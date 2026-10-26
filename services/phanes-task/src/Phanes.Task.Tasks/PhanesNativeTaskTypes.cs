namespace Phanes.Task.Tasks;

using System.Threading.Tasks;

/// <summary>task_type constants for first-party ("phanes.") tasks.</summary>
public static class PhanesNativeTaskTypes
{
    public const string Echo      = "phanes.echo";
    public const string Countdown = "phanes.countdown";
    public const string Flaky     = "phanes.flaky";
    public const string Slow      = "phanes.slow";
}
