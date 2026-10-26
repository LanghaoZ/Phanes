using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phanes.Task.Contracts;
using Phanes.Task.Sdk;
using Phanes.Task.Tasks;

namespace TasksTests;

public class DummyTaskTests
{
    private sealed class Context : ITaskContext
    {
        public string TaskType => PhanesNativeTaskTypes.Dummy;
        public ILogger Logger => NullLogger.Instance;
        public void Set<T>(string key, T value) where T : notnull { }
        public T Get<T>(string key) where T : notnull => throw new KeyNotFoundException(key);
    }

    [Fact]
    public async Task Echoes_ids_and_reports_ok()
    {
        var task = new DummyTask();
        var evt = new TaskEvent { TaskType = task.TaskType, TaskId = "t1", EventId = "e1" };

        var result = await task.ExecuteAsync(evt, new Context());

        Assert.True(result.Ok);
        Assert.Equal("t1", result.TaskId);
        Assert.Equal("e1", result.EventId);
        Assert.True(task.IsFinished());
    }
}
