using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phanes.Task.Contracts;
using Phanes.Task.Core.Consumer;
using Phanes.Task.Core.Model;
using Phanes.Task.Core.Registry;
using Phanes.Task.Sdk;

// Not under Phanes.Task.* on purpose: inside that namespace `Task` binds to
// the namespace instead of System.Threading.Tasks.Task.
namespace CoreTests;

internal sealed class FakeTask : ITask
{
    private int _inFlight;

    public FakeTask(string taskType = "test.fake") => TaskType = taskType;

    public string TaskType { get; }
    public ConcurrentQueue<string> Executed { get; } = new();
    public int MaxConcurrency { get; private set; }
    public Func<TaskEvent, CancellationToken, Task>? OnExecute { get; set; }
    public Func<bool> Finished { get; set; } = () => false;

    public void Configure(ITaskContext ctx) { }

    public async Task<TaskResult> ExecuteAsync(TaskEvent evt, ITaskContext ctx, CancellationToken ct = default)
    {
        var now = Interlocked.Increment(ref _inFlight);
        MaxConcurrency = Math.Max(MaxConcurrency, now);
        try
        {
            if (OnExecute is not null)
            {
                await OnExecute(evt, ct);
            }
            Executed.Enqueue(evt.EventId);
            return new TaskResult { TaskId = evt.TaskId, EventId = evt.EventId, Ok = true };
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    public bool IsFinished() => Finished();
}

internal sealed class FakeContext : ITaskContext
{
    public string TaskType => "test.fake";
    public ILogger Logger => NullLogger.Instance;
    public void Set<T>(string key, T value) where T : notnull { }
    public T Get<T>(string key) where T : notnull => throw new KeyNotFoundException(key);
}

internal sealed class FakeTaskRegistry : ITaskRegistry
{
    private readonly Dictionary<string, TaskRegistration> _map = new();

    public FakeTaskRegistry(params FakeTask[] tasks)
    {
        foreach (var task in tasks)
        {
            _map[task.TaskType] = new TaskRegistration(task, new FakeContext());
        }
    }

    public TaskRegistration Resolve(string taskType) => _map[taskType];
    public bool TryResolve(string taskType, out TaskRegistration registration)
        => _map.TryGetValue(taskType, out registration!);
    public IReadOnlyCollection<string> RegisteredTaskTypes => _map.Keys;
}

/// <summary>In-memory event source. Poll-thread hooks run inside Poll, like Kafka's.</summary>
internal sealed class FakeEventSource : ITaskEventSource
{
    private readonly ConcurrentQueue<ConsumedMessage> _messages = new();
    private readonly ConcurrentQueue<Action> _onPollThread = new();
    private readonly List<long> _stored = new();
    private long _nextOffset;

    public Action<IReadOnlyList<int>>? OnPartitionsAssigned { get; set; }
    public Action<IReadOnlyList<int>>? OnPartitionsRevoked { get; set; }
    public bool Opened { get; private set; }
    public bool Closed { get; private set; }

    public long[] StoredOffsets
    {
        get { lock (_stored) { return _stored.ToArray(); } }
    }

    public long Push(byte[]? value, int partition = 0)
    {
        var offset = Interlocked.Increment(ref _nextOffset) - 1;
        _messages.Enqueue(new ConsumedMessage(value, partition, offset));
        return offset;
    }

    public long Push(TaskEvent evt, int partition = 0)
        => Push(Google.Protobuf.MessageExtensions.ToByteArray(evt), partition);

    /// <summary>Run an action on the loop thread (from inside Poll) and wait for it.</summary>
    public Task RunOnPollThread(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _onPollThread.Enqueue(() =>
        {
            try { action(); done.SetResult(); }
            catch (Exception ex) { done.SetException(ex); }
        });
        return done.Task;
    }

    public void Open() => Opened = true;

    public ConsumedMessage? Poll(TimeSpan timeout)
    {
        while (_onPollThread.TryDequeue(out var action))
        {
            action();
        }
        if (_messages.TryDequeue(out var message))
        {
            return message;
        }
        Thread.Sleep(TimeSpan.FromMilliseconds(Math.Min(timeout.TotalMilliseconds, 5)));
        return null;
    }

    public void StoreOffset(ConsumedMessage message)
    {
        lock (_stored) { _stored.Add(message.Offset); }
    }

    public void Close() => Closed = true;
}

internal static class TestUtil
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    public static TaskEvent Event(string taskId, string eventId, string taskType = "test.fake")
        => new() { TaskType = taskType, TaskId = taskId, EventId = eventId };

    public static TaskEventEnvelope Envelope(string taskId, string eventId, long offset = 0)
        => new(Event(taskId, eventId), Partition: 0, Offset: offset);

    public static async Task Eventually(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting: {because}");
            }
            await Task.Delay(5);
        }
    }
}
