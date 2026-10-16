using ngxPMIpUpdater.Models;

namespace ngxPMIpUpdater.Services;

/// <summary>Decides which client rules the access list should end up with.</summary>
public static class ClientRules
{
    /// <summary>
    /// The lock-down rule. Needed because an empty client list does not mean "nobody": NPM only emits
    /// nginx's trailing "deny all;" when at least one client rule exists, so an empty list would open the
    /// proxy (or leave basic auth as the only gate).
    /// </summary>
    public static NpmClientEntry DenyAll => new() { Address = "all", Directive = "deny" };

    /// <summary>
    /// Merges dynamic and static addresses into allow rules. Returns null when nothing should be written.
    /// </summary>
    /// <param name="dynamicCidrs">Addresses from DNS, including still-valid fallbacks.</param>
    /// <param name="staticBlocks">Validated static blocks.</param>
    /// <param name="expiredDomains">
    /// Domains whose fallback expired this cycle. If they were the only source of addresses, the old IPs must
    /// still be revoked – so the list is locked down to <see cref="DenyAll"/> instead of being left untouched.
    /// </param>
    public static List<NpmClientEntry>? Build(
        IEnumerable<string> dynamicCidrs, IEnumerable<string> staticBlocks, IReadOnlyCollection<string> expiredDomains)
    {
        var allow = dynamicCidrs
            .Concat(staticBlocks)
            .Distinct()
            .Select(cidr => new NpmClientEntry { Address = cidr, Directive = "allow" })
            .ToList();

        if (allow.Count > 0)
            return allow;

        // Nothing configured at all is a setup problem, not a reason to lock everyone out
        return expiredDomains.Count > 0 ? [DenyAll] : null;
    }
}
