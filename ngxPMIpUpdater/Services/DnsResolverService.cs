using System.Net;
using Microsoft.Extensions.Logging;

namespace ngxPMIpUpdater.Services;

public class DnsResolverService(ILogger<DnsResolverService> logger)
{
    /// <summary>
    /// Resolves all IPv4 addresses for the given domains and returns them as /32 CIDR strings.
    /// </summary>
    public async Task<List<string>> ResolveDomainsAsync(IEnumerable<string> domains)
    {
        var result = new HashSet<string>();

        foreach (var domain in domains)
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(domain);
                foreach (var addr in addresses)
                {
                    // Only IPv4
                    if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        var cidr = $"{addr}/32";
                        result.Add(cidr);
                        logger.LogDebug("Resolved {Domain} -> {Cidr}", domain, cidr);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "DNS resolution failed for domain {Domain}", domain);
            }
        }

        return [.. result];
    }
}
