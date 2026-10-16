using Microsoft.Extensions.Options;
using ngxPMIpUpdater;
using ngxPMIpUpdater.Models;
using ngxPMIpUpdater.Services;
using Serilog;

// Docker HEALTHCHECK entry point – deliberately before any host or logging setup, so it stays fast
if (args is ["--healthcheck"])
    return HealthMonitor.Check(HealthMonitor.DefaultPath, DateTimeOffset.UtcNow, Console.Out);

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddSerilog((services, config) => config
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    // Bind -> PostConfigure -> Validate. Secret files are read before validation runs, so a token supplied
    // via ApiTokenFile satisfies the "exactly one auth path" rule.
    builder.Services.AddOptions<AppSettings>()
        .Bind(builder.Configuration.GetSection("AppSettings"))
        .PostConfigure(SecretFiles.Resolve)
        .ValidateOnStart();
    builder.Services.AddSingleton<IValidateOptions<AppSettings>, AppSettingsValidator>();

    // The default of 100 s blocks a whole cycle when NPM hangs
    builder.Services.AddHttpClient<NpmApiService>(client => client.Timeout = TimeSpan.FromSeconds(30));
    // Notifications must never hold up a cycle for long
    builder.Services.AddHttpClient<Notifier>(client => client.Timeout = TimeSpan.FromSeconds(10));
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<HealthMonitor>();
    builder.Services.AddSingleton<IpValidator>();
    builder.Services.AddSingleton<LastKnownIpStore>();
    builder.Services.AddSingleton<DnsResolverService>();
    builder.Services.AddSingleton<DynamicIpCollector>();
    builder.Services.AddHostedService<Worker>();

    var host = builder.Build();
    host.Run();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly.");
    return 1; // non-zero so restart policies and supervisors see the failure
}
finally
{
    Log.CloseAndFlush();
}
