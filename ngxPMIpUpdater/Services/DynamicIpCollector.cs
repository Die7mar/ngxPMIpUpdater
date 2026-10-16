using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ngxPMIpUpdater.Models;

namespace ngxPMIpUpdater.Services;

/// <summary>
/// Turns per-domain resolution results into the dynamic IP list, falling back to the last known good value
/// for domains that did not resolve.
///
/// The fallback is time-limited: a DynDNS address that DNS has not confirmed for longer than
/// <see cref="AppSettings.FallbackMaxAgeHours"/> may already be assigned to a stranger's connection, so it is
/// dropped from the list instead of being kept as an allow rule forever.
/// </summary>
public class DynamicIpCollector(
    ILogger<DynamicIpCollector> logger,
    IOptions<AppSettings> options,
    LastKnownIpStore lastKnownIps,
    TimeProvider time)
{
    private readonly int _maxAgeHours = options.Value.FallbackMaxAgeHours;

    /// <summary>
    /// Collects the dynamic addresses; <paramref name="expiredDomains"/> lists the domains whose fallback was
    /// dropped because it got too old.
    /// Returns false if any domain has no usable address and no last known value at all – writing the list
    /// then would silently revoke that domain's access before it ever had a confirmed address.
    /// </summary>
    public bool TryCollect(IEnumerable<DomainResolution> resolutions, out List<string> resolvedIps,
        out List<string> expiredDomains)
    {
        resolvedIps = [];
        expiredDomains = [];

        foreach (var resolution in resolutions)
        {
            if (resolution.Resolved)
            {
                lastKnownIps.Set(resolution.Domain, resolution.Cidrs);
                resolvedIps.AddRange(resolution.Cidrs);
                continue;
            }

            var cached = lastKnownIps.Get(resolution.Domain);
            if (cached is null || cached.Cidrs.Count == 0)
            {
                logger.LogError(
                    "Aborting update cycle: domain {Domain} did not resolve and has no last known IP. Updating the "
                    + "access list now would remove its access. Check DNS or remove the domain from the configuration.",
                    resolution.Domain);

                resolvedIps = [];
                expiredDomains = [];
                return false;
            }

            var age = time.GetUtcNow() - cached.ResolvedAt;
            if (_maxAgeHours > 0 && age > TimeSpan.FromHours(_maxAgeHours))
            {
                logger.LogError(
                    "Domain {Domain} has not resolved since {ResolvedAt:u} (more than {MaxAge} h). Removing its last "
                    + "known IP(s) from the access list – the address may belong to someone else by now.",
                    resolution.Domain, cached.ResolvedAt, _maxAgeHours);
                expiredDomains.Add(resolution.Domain);
                continue;
            }

            logger.LogWarning(
                "Domain {Domain} did not resolve – keeping its {Count} last known IP(s) from {ResolvedAt:u} so "
                + "access is not revoked.",
                resolution.Domain, cached.Cidrs.Count, cached.ResolvedAt);
            logger.LogDebug("Last known IP(s) for {Domain}: {Ips}",
                resolution.Domain, string.Join(", ", cached.Cidrs));
            resolvedIps.AddRange(cached.Cidrs);
        }

        resolvedIps = [.. resolvedIps.Distinct()];
        return true;
    }
}
