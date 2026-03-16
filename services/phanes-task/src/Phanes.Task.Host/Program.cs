using Autofac;
using Autofac.Extensions.DependencyInjection;
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

    // Configure Serilog from appsettings.json + environment, with file and console sinks.
    builder.Host.UseSerilog((ctx, services, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console(new CompactJsonFormatter())
        .WriteTo.File(
            formatter: new CompactJsonFormatter(),
            path: "logs/phanes-task-.log",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30));

    // TODO: register framework services (TaskLoop, Redis DAL) via TaskModule.cs

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
