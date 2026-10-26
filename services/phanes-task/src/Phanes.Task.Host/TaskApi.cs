using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Phanes.Task.Core.Options;
using Phanes.Task.Contracts;
using Phanes.Task.Core.Dispatch;
using Phanes.Task.Core.Registry;
using Phanes.Task.Core.Status;
using Phanes.Task.Core.Watermark;

namespace Phanes.Task.Host;

public sealed record InitTaskRequest(
    string TaskType,
    string? TaskId,
    string? EventId,
    string? Content,
    Dictionary<string, string>? Context);

public sealed record InitTaskResponse(string TaskType, string TaskId, string EventId);

/// <summary>
/// HTTP surface. Thin: submission goes through the dispatcher, status reads
/// Redis. Nothing here touches the actor registry — it is loop-thread-only.
/// </summary>
internal static class TaskApi
{
    public static void Map(WebApplication app)
    {
        var tasks = app.MapGroup("/tasks");

        tasks.MapPost("/init", async (
            InitTaskRequest request,
            ITaskTypeRegistry types,
            IExecutionStatusStore status,
            ITaskDispatcher dispatcher,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.TaskType) || !types.TryGet(request.TaskType, out _))
            {
                return Results.BadRequest(new { error = $"unknown task_type '{request.TaskType}'" });
            }

            var evt = new TaskEvent
            {
                TaskType = request.TaskType,
                TaskId = string.IsNullOrWhiteSpace(request.TaskId) ? Guid.NewGuid().ToString("N") : request.TaskId,
                EventId = string.IsNullOrWhiteSpace(request.EventId) ? Guid.NewGuid().ToString("N") : request.EventId,
                Content = request.Content ?? string.Empty,
            };
            if (request.Context is not null)
            {
                evt.Context.Add(request.Context);
            }

            var write = await status.TrySetAsync(evt.TaskType, evt.TaskId, evt.EventId, ExecutionStatus.Submitted, ct: ct);
            if (write != StatusWriteResult.Applied)
            {
                return Results.Conflict(new { error = "event_id already submitted", evt.TaskId, evt.EventId });
            }

            await dispatcher.SubmitAsync(evt, ct);
            return Results.Accepted($"/tasks/{evt.TaskType}/{evt.TaskId}/status",
                new InitTaskResponse(evt.TaskType, evt.TaskId, evt.EventId));
        })
        .WithName("InitTask");

        tasks.MapGet("/{taskType}/{taskId}/status", async (
            string taskType, string taskId, IExecutionStatusStore status, CancellationToken ct) =>
        {
            var events = await status.GetEventsAsync(taskType, taskId, ct);
            var killed = await status.IsKilledAsync(taskType, taskId, ct);
            if (events.Count == 0 && !killed)
            {
                return Results.NotFound();
            }
            return Results.Ok(new
            {
                task_type = taskType,
                task_id = taskId,
                killed,
                events = events.Select(e => new
                {
                    event_id = e.EventId,
                    status = e.Status.Wire(),
                    error = e.Error,
                    completion_reason = e.CompletionReason,
                    updated_at = e.UpdatedAt,
                }),
            });
        })
        .WithName("GetTaskStatus");

        tasks.MapPost("/{taskType}/{taskId}/kill", async (
            string taskType, string taskId, ITaskDispatcher dispatcher, CancellationToken ct) =>
        {
            await dispatcher.KillAsync(taskType, taskId, ct);
            return Results.Accepted();
        })
        .WithName("KillTask");

        app.MapGet("/task-types", (ITaskTypeRegistry types)
            => Results.Ok(types.All.Select(t => new { task_type = t.TaskType, clr_type = t.ClrType.FullName })));

        app.MapGet("/watermark", (IWatermarkManager watermark) => Results.Ok(watermark.Snapshot));

        // Effective options right now (appsettings + phanes-config overlay).
        app.MapGet("/options", (IOptionsMonitor<TaskOptions> options) => Results.Ok(options.CurrentValue));
    }
}
