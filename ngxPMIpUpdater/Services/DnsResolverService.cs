using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ngxPMIpUpdater.Models;

namespace ngxPMIpUpdater.Services;

/// <summary>Outcome of resolving a single domain.</summary>
/// <param name="Domain">The configured hostname.</param>
/// <param name="Cidrs">Usable addresses as CIDR strings; empty when <paramref name="Resolved"/> is false.</param>
/// <param name="Resolved">
/// False when the domain produced no usable address at all – either DNS failed or every answer was
/// discarded as bogus. The caller must fall back to the last known good value instead of silently
/// dropping this domain from the access list.
/// </param>
public record DomainResolution(string Domain, List<string> Cidrs, bool Resolved);

public class DnsResolverService(
    ILogger<DnsResolverService> logger,
    IOptions<AppSettings> options,
    IpValidator validator)
{
    private readonly bool _resolveIPv6 = options.Value.ResolveIPv6;

    /// <summary>
    /// Resolves each domain to CIDR strings (IPv4 as /32, and – if enabled – IPv6 as /128), reporting
    /// per-domain success so partial DNS failures stay visible to the caller.
    /// </summary>
    public async Task<List<DomainResolution>> ResolveDomainsAsync(
        IEnumerable<string> domains, CancellationToken cancellationToken = default)
    {
        var results = new List<DomainResolution>();

        foreach (var domain in domains)
        {
            var cidrs = new HashSet<string>();

            try
            {
                var addresses = await Dns.GetHostAddressesAsync(domain, cancellationToken);
                foreach (var addr in addresses)
                {
                    var prefix = addr.AddressFamily switch
                    {
                        AddressFamily.InterNetwork                     => 32,
                        AddressFamily.InterNetworkV6 when _resolveIPv6 => 128,
                        _                                              => 0
                    };

                    if (prefix == 0)
                        continue;

                    if (!validator.IsAcceptableDnsResult(addr, domain))
                        continue;

                    var cidr = $"{addr}/{prefix}";
                    cidrs.Add(cidr);
                    logger.LogDebug("Resolved {Domain} -> {Cidr}", domain, cidr);
                }

                if (cidrs.Count == 0)
                    logger.LogWarning("Domain {Domain} resolved, but yielded no usable address.", domain);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "DNS resolution failed for domain {Domain}", domain);
            }

            results.Add(new DomainResolution(domain, [.. cidrs], cidrs.Count > 0));
        }

        return results;
    }
}
