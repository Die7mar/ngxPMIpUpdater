using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ngxPMIpUpdater.Models;
using Microsoft.Extensions.Logging;

namespace ngxPMIpUpdater.Services;

public class NpmApiService(HttpClient httpClient, ILogger<NpmApiService> logger)
{
    private string? _sessionToken;
    private DateTime _sessionTokenExpiry = DateTime.MinValue;

    private async Task EnsureAuthenticatedAsync(NpmSettings settings, CancellationToken cancellationToken)
    {
        // Prefer long-lived API token (NPM v2.13+) – no login needed
        if (!string.IsNullOrWhiteSpace(settings.ApiToken))
        {
            if (httpClient.DefaultRequestHeaders.Authorization == null)
            {
                httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", settings.ApiToken);
                logger.LogInformation("Using static NPM API token for authentication.");
            }
            return;
        }

        // Fallback: login with email + password (session JWT, expires after ~1 h)
        if (_sessionToken != null && DateTime.UtcNow < _sessionTokenExpiry)
            return;

        logger.LogInformation("Authenticating with NPM at {BaseUrl} via email/password.", settings.BaseUrl);

        // Note: do NOT send "scope" – newer NPM versions return 400 if an unknown field is present
        var response = await httpClient.PostAsJsonAsync(
            $"{settings.BaseUrl.TrimEnd('/')}/api/tokens",
            new { identity = settings.Email, secret = settings.Password },
            cancellationToken);

        await EnsureSuccessOrLogAsync(response, "POST /api/tokens", cancellationToken);

        var tokenResponse = await response.Content.ReadFromJsonAsync<NpmTokenResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Empty token response from NPM.");

        _sessionToken = tokenResponse.Token;
        _sessionTokenExpiry = DateTime.UtcNow.AddMinutes(55); // NPM session tokens last ~1 h
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _sessionToken);

        logger.LogInformation("NPM session authentication successful.");
    }

    public async Task<NpmAccessList> GetAccessListAsync(NpmSettings settings,
        CancellationToken cancellationToken = default)
    {
        // "expand" is required – without it NPM omits items/clients entirely (see backend/internal/access-list.js)
        var url = $"{settings.BaseUrl.TrimEnd('/')}/api/nginx/access-lists/{settings.AccessListId}"
                  + "?expand=items,clients";

        var response = await SendWithAuthRetryAsync(
            settings,
            () => httpClient.GetAsync(url, cancellationToken),
            $"GET /api/nginx/access-lists/{settings.AccessListId}",
            cancellationToken);

        var list = await response.Content.ReadFromJsonAsync<NpmAccessList>(cancellationToken)
            ?? throw new InvalidOperationException("NPM returned null access list.");

        logger.LogInformation(
            "Fetched access list '{Name}' (ID {Id}) with {ClientCount} client(s) and {ItemCount} auth user(s).",
            list.Name, list.Id, list.Clients.Count, list.Items.Count);

        return list;
    }

    /// <summary>Writes the target clients. Returns false if the list already held them and nothing was sent.</summary>
    public async Task<bool> UpdateAccessListAsync(NpmSettings settings, NpmAccessList accessList,
        List<NpmClientEntry> newClients, CancellationToken cancellationToken = default)
    {
        var rules = string.Join(", ", newClients.Select(c => $"{c.Directive} {c.Address}"));

        if (IsUnchanged(accessList.Clients, newClients))
        {
            logger.LogInformation("Access list '{Name}' already holds the target {Count} rule(s) – skipping update.",
                accessList.Name, newClients.Count);
            logger.LogDebug("Unchanged rules: {Rules}", rules);
            return false;
        }

        // NPM's PUT schema does NOT accept "id" or other read-only fields – send only what's needed.
        //
        // "items" is deliberately omitted: NPM's update() deletes and recreates auth items whenever the
        // field is present, so sending it would wipe every basic-auth user of the access list. Leaving it
        // out keeps them untouched.
        var putBody = new
        {
            name        = accessList.Name,
            satisfy_any = accessList.SatisfyAny,
            pass_auth   = accessList.PassAuth,
            clients     = newClients
                            .Select(c => new { address = c.Address, directive = c.Directive })
                            .ToList()
        };

        var url = $"{settings.BaseUrl.TrimEnd('/')}/api/nginx/access-lists/{settings.AccessListId}";

        await SendWithAuthRetryAsync(
            settings,
            () => httpClient.PutAsJsonAsync(url, putBody, cancellationToken),
            $"PUT /api/nginx/access-lists/{settings.AccessListId}",
            cancellationToken);

        logger.LogInformation("Access list '{Name}' updated with {Count} rule(s).",
            accessList.Name, newClients.Count);
        logger.LogDebug("Written rules: {Rules}", rules);
        return true;
    }

    /// <summary>
    /// Compares current and target clients as order-insensitive sets. Deliberately strict: a false "equal"
    /// would suppress a needed update and could lock the operator out, while a false "different" only costs
    /// one nginx reload.
    /// </summary>
    private static bool IsUnchanged(List<NpmClientEntry> current, List<NpmClientEntry> target)
    {
        var currentSet = current.Select(c => $"{c.Directive} {c.Address}").ToHashSet();
        var targetSet  = target.Select(c => $"{c.Directive} {c.Address}").ToHashSet();

        return currentSet.SetEquals(targetSet) && currentSet.Count == current.Count;
    }

    /// <summary>
    /// Sends a request, retrying once after re-authenticating if NPM answers 401. Takes a factory rather
    /// than a request because an <see cref="HttpRequestMessage"/> cannot be sent twice.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithAuthRetryAsync(NpmSettings settings,
        Func<Task<HttpResponseMessage>> send, string endpoint, CancellationToken cancellationToken)
    {
        await EnsureAuthenticatedAsync(settings, cancellationToken);

        var response = await send();
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            await EnsureSuccessOrLogAsync(response, endpoint, cancellationToken);
            return response;
        }

        // A static token that gets rejected will be rejected again – retrying only hides the real cause.
        if (!string.IsNullOrWhiteSpace(settings.ApiToken))
        {
            logger.LogError(
                "NPM rejected the API token on {Endpoint} (HTTP 401). The token is most likely revoked or "
                + "wrong – recreate it in the NPM UI under Access → API Tokens.", endpoint);
            await EnsureSuccessOrLogAsync(response, endpoint, cancellationToken);
            return response;
        }

        logger.LogWarning("NPM returned 401 on {Endpoint}; re-authenticating and retrying once.", endpoint);

        _sessionToken = null;
        _sessionTokenExpiry = DateTime.MinValue;
        httpClient.DefaultRequestHeaders.Authorization = null;

        await EnsureAuthenticatedAsync(settings, cancellationToken);

        var retry = await send();
        await EnsureSuccessOrLogAsync(retry, $"{endpoint} (retry)", cancellationToken);
        return retry;
    }

    /// <summary>Throws if the response is not successful, logging the NPM error body first.</summary>
    private async Task EnsureSuccessOrLogAsync(HttpResponseMessage response, string endpoint,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        logger.LogError("NPM API error on {Endpoint}: HTTP {Status} – {Body}",
            endpoint, (int)response.StatusCode, body);

        response.EnsureSuccessStatusCode(); // throws HttpRequestException
    }
}
