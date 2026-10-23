using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ngxPMIpUpdater.Models;

namespace ngxPMIpUpdater.Services;

/// <summary>
/// Keeps the IP rules of a Cloudflare Zero Trust Access group in sync – the counterpart of the NPM access
/// list for services behind a Cloudflare Tunnel, where NPM only ever sees cloudflared's address.
///
/// Only the "ip" rules in the group's Include are managed. Every other rule (Include, Exclude, Require) is
/// passed through untouched, just like the basic-auth users of an NPM access list.
/// </summary>
public class CloudflareApiService(HttpClient httpClient, ILogger<CloudflareApiService> logger)
{
    public const string ApiBaseUrl = "https://api.cloudflare.com/client/v4";

    /// <summary>
    /// Stand-in for "deny all". The group is referenced by a Bypass/Allow policy, so it must keep matching
    /// nobody once every IP expired – but Cloudflare rejects a group without Include rules. The limited
    /// broadcast address is never the source of a real request, so this rule matches no one.
    /// </summary>
    public const string LockdownAddress = "255.255.255.255/32";

    public async Task<CloudflareAccessGroup> GetAccessGroupAsync(CloudflareSettings settings,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, GroupUrl(settings));
        var result = await SendAsync(settings, request, "GET access group", cancellationToken);

        var group = new CloudflareAccessGroup
        {
            Id = (string?)result["id"] ?? settings.AccessGroupId ?? string.Empty,
            Name = (string?)result["name"] ?? string.Empty,
            Raw = result
        };

        logger.LogInformation("Fetched Cloudflare Access group '{Name}' with {IpCount} IP rule(s) and {OtherCount} other rule(s).",
            group.Name, IpRules(group).Count(), Include(group).Count - IpRules(group).Count());

        return group;
    }

    /// <summary>
    /// The group's IP rules translated into the same form as NPM clients, so the change detection and
    /// notifications work identically for both targets.
    /// </summary>
    public static List<NpmClientEntry> CurrentClients(CloudflareAccessGroup group)
    {
        var addresses = IpRules(group).Select(rule => (string?)rule!["ip"]!["ip"] ?? string.Empty).ToList();

        return addresses is [LockdownAddress]
            ? [ClientRules.DenyAll]
            : [.. addresses.Select(a => new NpmClientEntry { Address = a, Directive = "allow" })];
    }

    /// <summary>Writes the target IP rules. Returns false if the group already held them and nothing was sent.</summary>
    public async Task<bool> UpdateAccessGroupAsync(CloudflareSettings settings, CloudflareAccessGroup group,
        List<NpmClientEntry> newClients, CancellationToken cancellationToken = default)
    {
        var addresses = newClients is [{ Directive: "deny" }]
            ? [LockdownAddress]
            : newClients.Where(c => c.Directive == "allow").Select(c => c.Address).ToList();

        var current = IpRules(group).Select(rule => (string?)rule!["ip"]!["ip"]).ToList();
        if (current.Count == addresses.Count && current.ToHashSet().SetEquals(addresses))
        {
            logger.LogInformation("Cloudflare Access group '{Name}' already holds the target {Count} IP rule(s) – skipping update.",
                group.Name, addresses.Count);
            return false;
        }

        // Non-IP rules first, in their original order; the IP rules are replaced as a whole
        var include = new JsonArray();
        foreach (var rule in Include(group).Where(rule => !IsIpRule(rule)))
            include.Add(rule!.DeepClone());
        foreach (var address in addresses)
            include.Add(new JsonObject { ["ip"] = new JsonObject { ["ip"] = address } });

        // Only the writable fields – the GET result also carries id, uid, created_at …
        var body = new JsonObject
        {
            ["name"] = group.Name,
            ["include"] = include,
            ["exclude"] = group.Raw["exclude"]?.DeepClone() ?? new JsonArray(),
            ["require"] = group.Raw["require"]?.DeepClone() ?? new JsonArray()
        };
        if (group.Raw["is_default"] is { } isDefault)
            body["is_default"] = isDefault.DeepClone();

        using var request = new HttpRequestMessage(HttpMethod.Put, GroupUrl(settings))
        {
            Content = JsonContent.Create(body)
        };
        await SendAsync(settings, request, "PUT access group", cancellationToken);

        logger.LogInformation("Cloudflare Access group '{Name}' updated with {Count} IP rule(s).",
            group.Name, addresses.Count);
        logger.LogDebug("Written IP rules: {Rules}", string.Join(", ", addresses));
        return true;
    }

    private static string GroupUrl(CloudflareSettings settings) =>
        $"{ApiBaseUrl}/accounts/{settings.AccountId}/access/groups/{settings.AccessGroupId}";

    private static JsonArray Include(CloudflareAccessGroup group) =>
        group.Raw["include"] as JsonArray ?? [];

    private static IEnumerable<JsonNode?> IpRules(CloudflareAccessGroup group) =>
        Include(group).Where(IsIpRule);

    private static bool IsIpRule(JsonNode? rule) =>
        rule is JsonObject obj && obj["ip"] is JsonObject ip && ip["ip"] is JsonValue;

    /// <summary>
    /// Sends the request and unwraps Cloudflare's {"success", "errors", "result"} envelope. Throws on any
    /// failure after logging Cloudflare's error messages.
    /// </summary>
    private async Task<JsonObject> SendAsync(CloudflareSettings settings, HttpRequestMessage request,
        string endpoint, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);

        JsonNode? envelope = null;
        try
        {
            envelope = JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            // handled below – a non-JSON answer is an error either way
        }

        if (response.IsSuccessStatusCode && (bool?)envelope?["success"] == true && envelope["result"] is JsonObject result)
            return result;

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            logger.LogError(
                "Cloudflare rejected the API token on {Endpoint} (HTTP {Status}). The token needs the permission "
                + "'Account → Access: Organizations, Identity Providers, and Groups → Edit' for this account.",
                endpoint, (int)response.StatusCode);

        var errors = envelope?["errors"] is JsonArray list && list.Count > 0
            ? string.Join("; ", list.Select(e => $"{e?["code"]}: {e?["message"]}"))
            : text;
        logger.LogError("Cloudflare API error on {Endpoint}: HTTP {Status} – {Errors}",
            endpoint, (int)response.StatusCode, errors);

        throw new HttpRequestException($"Cloudflare API call '{endpoint}' failed with HTTP {(int)response.StatusCode}.",
            null, response.StatusCode);
    }
}
