namespace Phanes.Task.Core.Status;

using System.Threading.Tasks;

/// <summary>
/// Per-event execution state machine. Transitions are monotonic by Rank:
/// Submitted → Received → Processing → one terminal state. The first
/// terminal write wins; later writes are rejected.
/// </summary>
public enum ExecutionStatus
{
    Unknown         = 0,
    Submitted       = 1,
    Received        = 2,
    Processing      = 3,
    Completed       = 10,
    Failed          = 11,
    FailedWithRetry = 12,
    Killed          = 13,
}

public static class ExecutionStatusExtensions
{
    public static bool IsTerminal(this ExecutionStatus s) => (int)s >= 10;

    /// <summary>Ordering used by the CAS: all terminals share one rank.</summary>
    public static int Rank(this ExecutionStatus s) => s.IsTerminal() ? 10 : (int)s;

    public static string Wire(this ExecutionStatus s) => s.ToString().ToLowerInvariant();

    public static ExecutionStatus Parse(string? wire)
        => Enum.TryParse<ExecutionStatus>(wire, ignoreCase: true, out var s) ? s : ExecutionStatus.Unknown;
}
