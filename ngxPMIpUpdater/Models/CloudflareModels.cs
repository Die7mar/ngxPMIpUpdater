using System.Text.Json.Nodes;

namespace ngxPMIpUpdater.Models;

/// <summary>
/// A Cloudflare Zero Trust Access group as returned by the API. Kept as raw JSON: the group may hold rule
/// types (emails, countries, IdP groups …) we do not model, and every one of them must survive the PUT.
/// </summary>
public class CloudflareAccessGroup
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>The "result" object of the GET response.</summary>
    public required JsonObject Raw { get; init; }
}
