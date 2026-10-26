using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Phanes.Task.Contracts;
using Phanes.Task.Core.Actor;
using Phanes.Task.Core.Consumer;
using Phanes.Task.Core.Dispatch;
using Phanes.Task.Core.Model;
using Phanes.Task.Core.Options;
using Phanes.Task.Core.Registry;
using Phanes.Task.Core.Status;
using Phanes.Task.Core.Watermark;
using Phanes.Task.Sdk;

// Not under Phanes.Task.* on purpose: inside that namespace `Task` binds to
// the namespace instead of System.Threading.Tasks.Task.
namespace CoreTests;

/// <summary>Shared across all FakeTask instances of a test so executions are observable.</summary>
internal sealed class ExecutionLog
{
    private int _inFlight;
    public ConcurrentQueue<(string TaskId, string EventId)> Executed { get; } = new();
    public int MaxConcurrency { get; private set; }
    public Func<TaskExecution, TaskEvent, CancellationToken, Task<TaskResult?>>? OnExecute { get; set; }
    public Func<FakeTask, bool> Finished { get; set; } = _ => false;

    public IEnumerable<string> EventIds => Executed.Select(e => e.EventId);

    internal void Enter() => MaxConcurrency = Math.Max(MaxConcurrency, Interlocked.Increment(ref _inFlight));
    internal void Exit() => Interlocked.Decrement(ref _inFlight);
}

internal sealed class FakeTask(string taskType, string taskId, ExecutionLog log) : ITask
{
    public string TaskType => taskType;
    public string TaskId => taskId;
    public int ExecutedCount { get; private set; }

    public async Task<TaskResult> ExecuteAsync(TaskExecution execution, TaskEvent evt, CancellationToken ct)
    {
        log.Enter();
        try
        {
            TaskResult? custom = null;
            if (log.OnExecute is not null)
            {
                custom = await log.OnExecute(execution, evt, ct);
            }
            ExecutedCount++;
            log.Executed.Enqueue((evt.TaskId, evt.EventId));
            return custom ?? TaskResults.Ok(execution);
        }
        finally
        {
            log.Exit();
        }
    }

    public bool IsFinished() => log.Finished(this);
}

internal sealed class FakeContext(string taskType) : ITaskContext
{
    public string TaskType => taskType;
    public ILogger Logger => NullLogger.Instance;
    public void Set<T>(string key, T value) where T : notnull { }
    public T Get<T>(string key) where T : notnull => throw new KeyNotFoundException(key);
}

internal sealed class FakeTypeRegistry(params string[] taskTypes) : ITaskTypeRegistry
{
    private readonly Dictionary<string, TaskTypeDescriptor> _types = taskTypes.ToDictionary(
        t => t, t => new TaskTypeDescriptor(t, typeof(FakeTask), new FakeContext(t), NullLogger.Instance));

    public bool TryGet(string taskType, out TaskTypeDescriptor descriptor) => _types.TryGetValue(taskType, out descriptor!);
    public IReadOnlyCollection<TaskTypeDescriptor> All => _types.Values;
}

/// <summary>Creates one FakeTask per actor; records scopes so disposal is observable.</summary>
internal sealed class FakeScopeFactory(ExecutionLog log, params string[] knownTypes) : IActorScopeFactory
{
    public ConcurrentBag<FakeScope> Created { get; } = new();

    public IActorScope Create(string taskType, string taskId)
    {
        if (!knownTypes.Contains(taskType))
        {
            throw new KeyNotFoundException(taskType);
        }
        var scope = new FakeScope(new FakeTask(taskType, taskId, log));
        Created.Add(scope);
        return scope;
    }

    internal sealed class FakeScope(FakeTask task) : IActorScope
    {
        public FakeTask FakeTask => task;
        public ITask Task => task;
        public bool Disposed { get; private set; }
        public IExecutionScope BeginExecution() => new Exec();
        public void Dispose() => Disposed = true;

        private sealed class Exec : IExecutionScope, IServiceProvider
        {
            public IServiceProvider Services => this;
            public object? GetService(Type serviceType) => null;
            public void Dispose() { }
        }
    }
}

internal sealed class FakePublisher : ITaskEventPublisher
{
    public ConcurrentQueue<TaskEvent> Published { get; } = new();
    public Func<TaskEvent, Exception?>? FailWith { get; set; }

    public Task PublishAsync(TaskEvent evt, CancellationToken ct)
    {
        var failure = FailWith?.Invoke(evt);
        if (failure is not null)
        {
            throw failure;
        }
        Published.Enqueue(evt);
        return Task.CompletedTask;
    }
}

/// <summary>In-memory event source. Poll-thread hooks run inside Poll, like Kafka's.</summary>
internal sealed class FakeEventSource : ITaskEventSource
{
    private readonly ConcurrentQueue<ConsumedMessage> _messages = new();
    private readonly ConcurrentQueue<Action> _onPollThread = new();
    private readonly ConcurrentDictionary<int, long> _positions = new();
    private long _nextOffset;

    public Action<IReadOnlyList<int>>? OnPartitionsAssigned { get; set; }
    public Action<IReadOnlyList<int>>? OnPartitionsRevoked { get; set; }
    public bool Opened { get; private set; }
    public bool Closed { get; private set; }

    /// <summary>Next offset to read, per partition, as last stored.</summary>
    public IReadOnlyDictionary<int, long> Positions => _positions;

    public long Push(byte[]? value, int partition = 0)
    {
        var offset = Interlocked.Increment(ref _nextOffset) - 1;
        _messages.Enqueue(new ConsumedMessage(value, partition, offset));
        return offset;
    }

    public long Push(TaskEvent evt, int partition = 0)
        => Push(Google.Protobuf.MessageExtensions.ToByteArray(evt), partition);

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

    public void StorePosition(int partition, long nextOffset) => _positions[partition] = nextOffset;

    public void Close() => Closed = true;
}

internal static class TestUtil
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    public static IOptions<TaskOptions> Options(Action<WorkerOptions>? _ = null, WorkerOptions? worker = null, KafkaOptions? kafka = null, DispatcherOptions? dispatcher = null)
        => Microsoft.Extensions.Options.Options.Create(new TaskOptions
        {
            Worker = worker ?? new WorkerOptions(),
            Kafka = kafka ?? new KafkaOptions { PollTimeoutMs = 5 },
            Dispatcher = dispatcher ?? new DispatcherOptions(),
        });

    public static WorkerOptions FastWorker(double idleSeconds = 300, int mailbox = 8, int maxChainDepth = 64) => new()
    {
        ActorIdleTimeoutSeconds = idleSeconds,
        ActorScanIntervalSeconds = 0.01,
        WatermarkCommitIntervalSeconds = 0.01,
        WatermarkStuckSeconds = 0.05,
        ActorMailboxCapacity = mailbox,
        MaxChainDepth = maxChainDepth,
    };

    public static WatermarkManager Watermark(WorkerOptions? worker = null)
        => new(Options(worker: worker ?? FastWorker()), NullLogger<WatermarkManager>.Instance);

    public static TaskEvent Event(string taskId, string eventId, string taskType = "test.fake", string content = "")
        => new() { TaskType = taskType, TaskId = taskId, EventId = eventId, Content = content };

    public static TaskEventEnvelope Envelope(string taskId, string eventId, long offset = 0, int partition = 0, string taskType = "test.fake")
        => new(Event(taskId, eventId, taskType), partition, offset);

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

    public static async Task<ExecutionStatus> StatusOf(IExecutionStatusStore store, string taskType, string taskId, string eventId)
        => (await store.GetEventsAsync(taskType, taskId)).Single(e => e.EventId == eventId).Status;
}
