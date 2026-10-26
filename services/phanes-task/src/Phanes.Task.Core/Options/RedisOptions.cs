namespace Phanes.Task.Core.Options;

using System.Threading.Tasks;

/// <summary>Execution-status store. Empty ConnectionString → in-memory store (dev/tests only).</summary>
public sealed class RedisOptions
{
    public string ConnectionString { get; set; } = string.Empty;
    public string KeyPrefix        { get; set; } = "phanes:task";
    public int    StatusTtlSeconds { get; set; } = 7 * 24 * 3600;
}
