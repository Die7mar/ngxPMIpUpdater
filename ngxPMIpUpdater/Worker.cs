using Microsoft.Extensions.Options;
using ngxPMIpUpdater.Models;
using ngxPMIpUpdater.Services;

namespace ngxPMIpUpdater;

public class Worker(
    ILogger<Worker> logger,
    IOptions<AppSettings> options,
    DnsResolverService dnsResolver,
    IpValidator ipValidator,
    LastKnownIpStore lastKnownIps,
    NpmApiService npmApi) : BackgroundService
{
    private readonly AppSettings _settings = options.Value;

    /// <summary>Retry sooner than the regular interval when a cycle could not be completed.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ngxPMIpUpdater started. Interval: {Minutes} min, Domains: {Domains}",
            _settings.IntervalMinutes, string.Join(", ", ConfigList.Flatten(_settings.Domains)));

        while (!stoppingToken.IsCancellationRequested)
        {
            var completed = await RunUpdateCycleAsync(stoppingToken);

            var interval = TimeSpan.FromMinutes(_settings.IntervalMinutes);
            var delay = completed ? interval : Min(RetryDelay, interval);

            if (!completed)
                logger.LogWarning("Update cycle did not complete. Retrying in {Minutes} min.",
                    delay.TotalMinutes);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break; // shutting down – not an error
            }
        }

        logger.LogInformation("ngxPMIpUpdater stopped.");
    }

    /// <summary>Runs one cycle. Returns false if the access list was deliberately left untouched.</summary>
    private async Task<bool> RunUpdateCycleAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting IP update cycle at {Time}", DateTimeOffset.Now);
        try
        {
            var domains = ConfigList.Flatten(_settings.Domains);

            // Validate operator-configured blocks; a single bad entry is dropped instead of failing the
            // whole PUT, which is what used to break the update completely.
            var staticBlocks = new List<string>();
            foreach (var raw in ConfigList.Flatten(_settings.StaticIpBlocks))
                if (ipValidator.TryNormalizeBlock(raw, _settings.AllowOpenBlocks, out var cidr))
                    staticBlocks.Add(cidr);

            var resolutions = await dnsResolver.ResolveDomainsAsync(domains, cancellationToken);

            if (!TryCollectDynamicIps(resolutions, out var resolvedIps))
                return false;

            // Merge dynamic IPs with static blocks (deduplicated)
            var allIps = resolvedIps
                .Concat(staticBlocks)
                .Distinct()
                .ToList();

            if (allIps.Count == 0)
            {
                logger.LogWarning("No IPs resolved and no static blocks configured. Skipping NPM update to avoid locking out all clients.");
                return false;
            }

            if (staticBlocks.Count > 0)
                logger.LogDebug("Static IP blocks included: {Blocks}", string.Join(", ", staticBlocks));

            var accessList = await npmApi.GetAccessListAsync(_settings.Npm, cancellationToken);
            await npmApi.UpdateAccessListAsync(_settings.Npm, accessList, allIps, cancellationToken);

            logger.LogInformation("Update cycle completed. {Count} IPs ({Dynamic} dynamic, {Static} static).",
                allIps.Count, resolvedIps.Count, staticBlocks.Count);

            return true;
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Update cycle cancelled – shutting down.");
            return true; // not a failure; the loop exits on the stopping token anyway
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during update cycle.");
            return false;
        }
    }

    /// <summary>
    /// Turns per-domain resolution results into the dynamic IP list, falling back to the last known good
    /// value for domains that did not resolve. Returns false if any domain has no usable address at all –
    /// writing the list then would silently revoke that domain's access.
    /// </summary>
    private bool TryCollectDynamicIps(List<DomainResolution> resolutions, out List<string> resolvedIps)
    {
        resolvedIps = [];

        foreach (var resolution in resolutions)
        {
            if (resolution.Resolved)
            {
                lastKnownIps.Set(resolution.Domain, resolution.Cidrs);
                resolvedIps.AddRange(resolution.Cidrs);
                continue;
            }

            var cached = lastKnownIps.Get(resolution.Domain);
            if (cached is { Count: > 0 })
            {
                logger.LogWarning(
                    "Domain {Domain} did not resolve – keeping its {Count} last known IP(s) so access is not revoked.",
                    resolution.Domain, cached.Count);
                logger.LogDebug("Last known IP(s) for {Domain}: {Ips}",
                    resolution.Domain, string.Join(", ", cached));
                resolvedIps.AddRange(cached);
                continue;
            }

            logger.LogError(
                "Aborting update cycle: domain {Domain} did not resolve and has no last known IP. Updating the "
                + "access list now would remove its access. Check DNS or remove the domain from the configuration.",
                resolution.Domain);

            resolvedIps = [];
            return false;
        }

        resolvedIps = [.. resolvedIps.Distinct()];
        return true;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
