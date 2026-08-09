using Microsoft.Extensions.Options;
using ngxPMIpUpdater;
using ngxPMIpUpdater.Models;
using ngxPMIpUpdater.Services;
using Serilog;

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
    builder.Services.AddSingleton<IpValidator>();
    builder.Services.AddSingleton<LastKnownIpStore>();
    builder.Services.AddSingleton<DnsResolverService>();
    builder.Services.AddHostedService<Worker>();

    var host = builder.Build();
    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly.");
}
finally
{
    Log.CloseAndFlush();
}
