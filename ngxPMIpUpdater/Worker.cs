using Microsoft.Extensions.Options;
using ngxPMIpUpdater.Models;
using ngxPMIpUpdater.Services;

namespace ngxPMIpUpdater;

public class Worker(
    ILogger<Worker> logger,
    IOptions<AppSettings> options,
    DnsResolverService dnsResolver,
    NpmApiService npmApi) : BackgroundService
{
    private readonly AppSettings _settings = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ngxPMIpUpdater started. Interval: {Minutes} min, Domains: {Domains}",
            _settings.IntervalMinutes, string.Join(", ", _settings.Domains));

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunUpdateCycleAsync();
            await Task.Delay(TimeSpan.FromMinutes(_settings.IntervalMinutes), stoppingToken);
        }
    }

    private async Task RunUpdateCycleAsync()
    {
        logger.LogInformation("Starting IP update cycle at {Time}", DateTimeOffset.Now);
        try
        {
            // Filter empty entries that come from unset .env variables (DOMAIN_2=, STATIC_IP_2=)
            var domains     = _settings.Domains.Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
            var staticBlocks = _settings.StaticIpBlocks.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

            var resolvedIps = await dnsResolver.ResolveDomainsAsync(domains);

            // Merge dynamic IPs with static blocks (deduplicated)
            var allIps = resolvedIps
                .Concat(staticBlocks)
                .Distinct()
                .ToList();

            if (allIps.Count == 0)
            {
                logger.LogWarning("No IPs resolved and no static blocks configured. Skipping NPM update to avoid locking out all clients.");
                return;
            }

            if (staticBlocks.Count > 0)
                logger.LogInformation("Static IP blocks included: {Blocks}",
                    string.Join(", ", staticBlocks));

            var accessList = await npmApi.GetAccessListAsync(_settings.Npm);
            await npmApi.UpdateAccessListAsync(_settings.Npm, accessList, allIps);

            logger.LogInformation("Update cycle completed. {Count} IPs written ({Dynamic} dynamic, {Static} static).",
                allIps.Count, resolvedIps.Count, staticBlocks.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during update cycle.");
        }
    }
}
