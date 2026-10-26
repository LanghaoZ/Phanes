namespace Phanes.Task.Core.Options;

using System.Threading.Tasks;

/// <summary>Execution-status store. Empty ConnectionString → in-memory store (dev/tests only).</summary>
public sealed class RedisOptions
{
    public string ConnectionString { get; init; } = string.Empty;
    public string KeyPrefix        { get; init; } = "phanes:task";
    public int    StatusTtlSeconds { get; init; } = 7 * 24 * 3600;
}
