using Microsoft.Extensions.Logging;

namespace Phanes.Task.Sdk;

/// <summary>
/// Shared execution context scoped to a TaskType, not to a single execution.
/// One instance is created per registered TaskType and shared across all concurrent
/// executions of that type, making it the right place to hold shared infrastructure
/// resources such as connection pools and typed clients.
///
/// Lifecycle:
///   1. Framework creates one ITaskContext per registered TaskType at startup.
///   2. ITask.Configure(ctx) is called once — the task registers its
///      shared resources via Set&lt;T&gt;.
///   3. The context is sealed before any execution begins; Set&lt;T&gt; must not
///      be called after Configure returns.
///   4. All ITask.ExecuteAsync calls receive the same context instance.
///
/// Well-known keys pre-populated by the framework before Configure is called:
///   "redis"  — StackExchange.Redis IDatabase for the shared Redis connection
/// </summary>
public interface ITaskContext
{
    string  TaskType { get; }
    ILogger Logger   { get; }

    /// <summary>
    /// Register a shared resource during Configure. Must not be called after
    /// the context is sealed (i.e. after Configure returns).
    /// </summary>
    void Set<T>(string key, T value) where T : notnull;

    /// <summary>
    /// Retrieve a shared resource by key. Throws KeyNotFoundException if the
    /// key was not registered during Configure.
    /// </summary>
    T Get<T>(string key) where T : notnull;
}
