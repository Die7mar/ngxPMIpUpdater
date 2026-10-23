using Microsoft.Extensions.Options;
using ngxPMIpUpdater.Models;
using ngxPMIpUpdater.Services;

namespace ngxPMIpUpdater;

public class Worker(
    ILogger<Worker> logger,
    IOptions<AppSettings> options,
    DnsResolverService dnsResolver,
    IpValidator ipValidator,
    DynamicIpCollector dynamicIps,
    NpmApiService npmApi,
    CloudflareApiService cloudflareApi,
    HealthMonitor health,
    Notifier notifier) : BackgroundService
{
    private readonly AppSettings _settings = options.Value;

    /// <summary>Retry sooner than the regular interval when a cycle could not be completed.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var targets = new[] { _settings.UsesNpm ? "NPM" : null, _settings.UsesCloudflare ? "Cloudflare" : null }
            .OfType<string>();
        logger.LogInformation("ngxPMIpUpdater started. Interval: {Minutes} min, Targets: {Targets}, Domains: {Domains}",
            _settings.IntervalMinutes, string.Join(" + ", targets), string.Join(", ", ConfigList.Flatten(_settings.Domains)));

        while (!stoppingToken.IsCancellationRequested)
        {
            var completed = await RunUpdateCycleAsync(stoppingToken);
            if (stoppingToken.IsCancellationRequested)
                break;

            await ReportHealthAsync(completed, stoppingToken);

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

            if (!dynamicIps.TryCollect(resolutions, out var resolvedIps, out var expiredDomains))
                return false;

            var clients = ClientRules.Build(resolvedIps, staticBlocks, expiredDomains);
            if (clients == null)
            {
                logger.LogWarning("No IPs resolved and no static blocks configured. Skipping the update to avoid locking out all clients.");
                return false;
            }

            if (clients is [{ Directive: "deny" }])
                logger.LogError(
                    "No usable IP left: the last known IP(s) of {Domains} expired and no static blocks are configured. "
                    + "Locking the access list down to 'deny all' until DNS resolves again.",
                    string.Join(", ", expiredDomains));

            if (staticBlocks.Count > 0)
                logger.LogDebug("Static IP blocks included: {Blocks}", string.Join(", ", staticBlocks));

            // Each target on its own: Cloudflare being down must not keep NPM from getting the new IP
            var completed = true;

            if (_settings.UsesNpm)
                completed &= await ApplyToTargetAsync("NPM", async () =>
                {
                    var accessList = await npmApi.GetAccessListAsync(_settings.Npm, cancellationToken);
                    var before = accessList.Clients.ToList();
                    var written = await npmApi.UpdateAccessListAsync(_settings.Npm, accessList, clients, cancellationToken);
                    return new TargetUpdate($"Access list '{accessList.Name}'", before, written,
                        "Basic-auth users can still log in.");
                }, clients, expiredDomains, cancellationToken);

            if (_settings.UsesCloudflare)
                completed &= await ApplyToTargetAsync("Cloudflare", async () =>
                {
                    var group = await cloudflareApi.GetAccessGroupAsync(_settings.Cloudflare, cancellationToken);
                    var before = CloudflareApiService.CurrentClients(group);
                    var written = await cloudflareApi.UpdateAccessGroupAsync(_settings.Cloudflare, group, clients,
                        cancellationToken);
                    return new TargetUpdate($"Cloudflare Access group '{group.Name}'", before, written,
                        "Other Access policies (e.g. login) still apply.");
                }, clients, expiredDomains, cancellationToken);

            if (completed)
                logger.LogInformation("Update cycle completed. {Count} rule(s) ({Dynamic} dynamic, {Static} static).",
                    clients.Count, resolvedIps.Count, staticBlocks.Count);

            return completed;
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

    private record TargetUpdate(string Description, List<NpmClientEntry> Before, bool Written, string LockdownNote);

    /// <summary>Writes the rules to one target and reports the change. Returns false if that target failed.</summary>
    private async Task<bool> ApplyToTargetAsync(string target, Func<Task<TargetUpdate>> apply,
        List<NpmClientEntry> clients, IReadOnlyCollection<string> expiredDomains, CancellationToken cancellationToken)
    {
        try
        {
            var update = await apply();

            if (update.Written && ChangeNotification.Describe(update.Description, update.Before, clients,
                    expiredDomains, notifier.NotifyOnChange, update.LockdownNote) is { } change)
                await notifier.SendAsync(change.Level, change.Title, change.Message, cancellationToken);

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Updating {Target} failed.", target);
            return false;
        }
    }

    /// <summary>
    /// Feeds the cycle result into the health check and reports the moment the service turns unhealthy –
    /// and when it recovers – instead of every single failed attempt, which a retry often fixes.
    /// </summary>
    private async Task ReportHealthAsync(bool completed, CancellationToken cancellationToken)
    {
        if (completed)
        {
            if (health.RecordSuccess())
                await notifier.SendAsync(NotificationLevel.Info, "Updates working again",
                    "ngxPMIpUpdater completed an update cycle again.", cancellationToken);
            return;
        }

        if (!health.RecordFailure())
            return;

        var since = health.LastSuccess is { } last ? $"since {last:u}" : "since startup";
        logger.LogError("No successful update cycle {Since}. The health check now reports unhealthy.", since);
        await notifier.SendAsync(NotificationLevel.Warning, "Updates failing",
            $"ngxPMIpUpdater has not completed an update cycle {since}. The access list is not being kept up to "
            + "date – check the container logs.", cancellationToken);
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
