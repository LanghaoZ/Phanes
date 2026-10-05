using Autofac;
using Autofac.Extensions.DependencyInjection;
using Phanes.Task.Core.Consumer;
using Phanes.Task.Core.Options;
using Phanes.Task.Host;
using Serilog;
using Serilog.Formatting.Compact;

// Bootstrap logger captures startup errors before full configuration loads.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());
    builder.Host.ConfigureContainer<ContainerBuilder>(b => b.RegisterModule<TaskModule>());

    // Configure Serilog from appsettings.json + environment.
    // Console: plain text for human readability during dev.
    // File:    structured JSON for log shipping / grep / jq.
    builder.Host.UseSerilog((ctx, services, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console(outputTemplate:
            "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
        .WriteTo.File(
            formatter: new CompactJsonFormatter(),
            path: "logs/phanes-task-.log",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30));

    // Framework services (TaskRegistry, ActorRegistry, ...) are registered
    // via TaskModule on the Autofac container. TaskLoop is added here as a
    // hosted background service so the host can drive its lifecycle.
    builder.Services.Configure<TaskOptions>(
        builder.Configuration.GetSection("PhanesTask"));
    builder.Services.AddHostedService<TaskLoop>();

    builder.Services.AddEndpointsApiExplorer();

    var app = builder.Build();

    app.UseSerilogRequestLogging();

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
}
catch (Exception ex)
{
    Log.Fatal(ex, "phanes-task failed to start");
}
finally
{
    Log.CloseAndFlush();
}
