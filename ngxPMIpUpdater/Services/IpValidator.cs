using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace ngxPMIpUpdater.Services;

/// <summary>
/// Validates everything that is about to become an nginx "allow" rule.
///
/// Two distinct jobs, because the two input sources deserve different trust levels:
/// <list type="bullet">
/// <item><see cref="TryNormalizeBlock"/> – operator-configured static blocks. Checks syntax only (plus a
/// guard against opening the proxy to the whole internet); private ranges are the documented main use case
/// and must stay allowed.</item>
/// <item><see cref="IsAcceptableDnsResult"/> – addresses that came out of DNS. Filters answers that cannot
/// plausibly be a real client, e.g. the 0.0.0.0 that DynDNS providers hand out for expired hostnames.</item>
/// </list>
/// </summary>
public class IpValidator(ILogger<IpValidator> logger)
{
    /// <summary>
    /// Trims and normalises a configured address or CIDR block into NPM's expected form.
    /// Returns false for anything unusable – the caller drops that entry and carries on.
    /// </summary>
    public bool TryNormalizeBlock(string? raw, bool allowOpenBlocks, out string cidr)
    {
        cidr = string.Empty;

        // Trailing whitespace and stray \r from .env files are the most common cause of a rejected update
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value))
            return false; // unset placeholder (STATIC_IP_2=) – expected, not worth logging

        var parts = value.Split('/');
        if (parts.Length > 2 || !TryParseAddress(parts[0], out var address))
        {
            logger.LogError("Ignoring invalid static IP block {Value}: not a valid IP address or CIDR block.",
                value);
            return false;
        }

        var maxPrefix = address.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        var prefix = maxPrefix;

        if (parts.Length == 2 && (!int.TryParse(parts[1], out prefix) || prefix < 0 || prefix > maxPrefix))
        {
            logger.LogError("Ignoring invalid static IP block {Value}: prefix length must be between 0 and {Max}.",
                value, maxPrefix);
            return false;
        }

        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            if (prefix == 0 && allowOpenBlocks)
            {
                logger.LogWarning(
                    "Static IP block {Value} allows the ENTIRE internet. Accepted only because AllowOpenBlocks is enabled.",
                    value);
                cidr = $"{address}/0";
                return true;
            }

            logger.LogCritical(
                "Refusing static IP block {Value}: it would open the proxy to the entire internet. "
                + "Set AppSettings:AllowOpenBlocks to true if this is really intended.",
                value);
            return false;
        }

        // nginx masks meaningless host bits with a warning rather than failing; do the same up front so
        // NPM's stricter validation never sees e.g. 192.168.1.5/24.
        var network = Mask(address, prefix);
        if (!network.Equals(address))
            logger.LogWarning("Static IP block {Value} has host bits set; normalised to {Cidr}.",
                value, $"{network}/{prefix}");

        cidr = $"{network}/{prefix}";
        return true;
    }

    /// <summary>
    /// Decides whether a DNS answer can plausibly be a real remote client. Rejects the bogus answers that
    /// expired DynDNS hostnames, NXDOMAIN-rewriting resolvers and captive portals hand out – those would
    /// otherwise silently become allow rules.
    /// </summary>
    public bool IsAcceptableDnsResult(IPAddress address, string domain)
    {
        var reason = address switch
        {
            _ when address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
                => "unspecified address (typical answer for an expired DynDNS hostname)",
            _ when IPAddress.IsLoopback(address)             => "loopback address",
            _ when address.Equals(IPAddress.Broadcast)       => "broadcast address",
            _ when IsIPv4LinkLocal(address)                  => "IPv4 link-local address",
            _ when address.IsIPv6LinkLocal                   => "IPv6 link-local address",
            _ when IsIPv4Multicast(address)                  => "IPv4 multicast address",
            _ when address.IsIPv6Multicast                   => "IPv6 multicast address",
            _                                                => null
        };

        if (reason == null)
            return true;

        logger.LogWarning("Discarding DNS result {Address} for {Domain}: {Reason}.", address, domain, reason);
        return false;
    }

    /// <summary>Strict IP parsing – rejects shorthand forms like "10.1" that must not become allow rules.</summary>
    private static bool TryParseAddress(string value, out IPAddress address)
    {
        if (!IPAddress.TryParse(value, out var parsed))
        {
            address = IPAddress.None;
            return false;
        }

        if (parsed.AddressFamily == AddressFamily.InterNetwork && value.Split('.').Length != 4)
        {
            address = IPAddress.None;
            return false;
        }

        address = parsed;
        return parsed.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6;
    }

    /// <summary>Zeroes the host bits below <paramref name="prefix"/>.</summary>
    private static IPAddress Mask(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();

        for (var i = 0; i < bytes.Length; i++)
        {
            var bitsForByte = prefix - i * 8;
            bytes[i] = bitsForByte switch
            {
                >= 8 => bytes[i],
                <= 0 => 0,
                _    => (byte)(bytes[i] & (0xFF << (8 - bitsForByte)))
            };
        }

        return new IPAddress(bytes);
    }

    private static bool IsIPv4LinkLocal(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork
        && address.GetAddressBytes() is [169, 254, _, _];

    private static bool IsIPv4Multicast(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork
        && address.GetAddressBytes() is [>= 224 and <= 239, _, _, _];
}
