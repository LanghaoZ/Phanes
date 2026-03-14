var builder = WebApplication.CreateBuilder(args);

// TODO: configure Serilog
// TODO: register PhanesTask services (TaskLoop, ActorRegistry, WatermarkManager, TaskFactory, Redis DAL)
// TODO: register IHostedService for TaskLoop

builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();

// Task API endpoints
var tasks = app.MapGroup("/tasks");

tasks.MapPost("/init", () =>
{
    // TODO: accept InitTaskRequest, publish TaskEvent to Kafka, return task_id
    return Results.StatusCode(501);
})
.WithName("InitTask");

tasks.MapGet("/{taskId}/status", (string taskId) =>
{
    // TODO: read from Redis (fast path) or MySQL (fallback), return task status
    return Results.StatusCode(501);
})
.WithName("GetTaskStatus");

app.Run();
