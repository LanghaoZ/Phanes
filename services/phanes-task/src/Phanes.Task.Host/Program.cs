using Autofac;
using Autofac.Extensions.DependencyInjection;
using Phanes.Task.Core.Configuration;
using Phanes.Task.Core.Consumer;
using Phanes.Task.Core.Options;
using Phanes.Task.Core.Registry;
using Phanes.Task.Sdk;
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

    // Dynamic configuration: phanes-config document "phanes-task/options"
    // layered over appsettings (hot-reloaded via IOptionsMonitor). Disabled
    // when PhanesTask:ConfigService:Url is empty.
    builder.Configuration.AddPhanesConfig((message, ex) =>
    {
        if (ex is null) Log.Information(message); else Log.Warning(ex, message);
    });

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

    // Type-level shared setup (ITaskTypeSetup.Configure) runs once, now that
    // the container exists; contexts are sealed afterwards.
    var root = app.Services.GetAutofacRoot();
    root.Resolve<TaskTypeRegistry>()
        .ConfigureSharedContexts(descriptor =>
        {
            using var scope = root.BeginLifetimeScope(AutofacActorScopeFactory.ActorTag);
            return scope.ResolveKeyed<ITask>(descriptor.TaskType) as ITaskTypeSetup;
        });

    app.UseSerilogRequestLogging();
    TaskApi.Map(app);

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
