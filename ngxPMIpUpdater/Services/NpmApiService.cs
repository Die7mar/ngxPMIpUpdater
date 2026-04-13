using System.Net.Http.Headers;
using System.Net.Http.Json;
using ngxPMIpUpdater.Models;
using Microsoft.Extensions.Logging;

namespace ngxPMIpUpdater.Services;

public class NpmApiService(HttpClient httpClient, ILogger<NpmApiService> logger)
{
    private string? _sessionToken;
    private DateTime _sessionTokenExpiry = DateTime.MinValue;

    private async Task EnsureAuthenticatedAsync(NpmSettings settings)
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
            new { identity = settings.Email, secret = settings.Password });

        await EnsureSuccessOrLogAsync(response, "POST /api/tokens");

        var tokenResponse = await response.Content.ReadFromJsonAsync<NpmTokenResponse>()
            ?? throw new InvalidOperationException("Empty token response from NPM.");

        _sessionToken = tokenResponse.Token;
        _sessionTokenExpiry = DateTime.UtcNow.AddMinutes(55); // NPM session tokens last ~1 h
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _sessionToken);

        logger.LogInformation("NPM session authentication successful.");
    }

    public async Task<NpmAccessList> GetAccessListAsync(NpmSettings settings)
    {
        await EnsureAuthenticatedAsync(settings);

        var response = await httpClient.GetAsync(
            $"{settings.BaseUrl.TrimEnd('/')}/api/nginx/access-lists/{settings.AccessListId}");

        await EnsureSuccessOrLogAsync(response, $"GET /api/nginx/access-lists/{settings.AccessListId}");

        var list = await response.Content.ReadFromJsonAsync<NpmAccessList>()
            ?? throw new InvalidOperationException("NPM returned null access list.");

        logger.LogInformation("Fetched access list '{Name}' (ID {Id}) with {Count} client(s).",
            list.Name, list.Id, list.Clients.Count);

        return list;
    }

    public async Task UpdateAccessListAsync(NpmSettings settings, NpmAccessList accessList,
        List<string> newCidrAddresses)
    {
        await EnsureAuthenticatedAsync(settings);

        // NPM's PUT schema does NOT accept "id" or other read-only fields – send only what's needed
        var putBody = new
        {
            name        = accessList.Name,
            satisfy_any = accessList.SatisfyAny,
            pass_auth   = accessList.PassAuth,
            items       = accessList.Items,
            clients     = newCidrAddresses
                            .Select(addr => new { address = addr, directive = "allow" })
                            .ToList()
        };

        var response = await httpClient.PutAsJsonAsync(
            $"{settings.BaseUrl.TrimEnd('/')}/api/nginx/access-lists/{settings.AccessListId}",
            putBody);

        await EnsureSuccessOrLogAsync(response, $"PUT /api/nginx/access-lists/{settings.AccessListId}");

        logger.LogInformation("Access list '{Name}' updated with {Count} IP(s): {IPs}",
            accessList.Name, newCidrAddresses.Count, string.Join(", ", newCidrAddresses));
    }

    /// <summary>Throws if the response is not successful, logging the NPM error body first.</summary>
    private async Task EnsureSuccessOrLogAsync(HttpResponseMessage response, string endpoint)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync();
        logger.LogError("NPM API error on {Endpoint}: HTTP {Status} – {Body}",
            endpoint, (int)response.StatusCode, body);

        response.EnsureSuccessStatusCode(); // throws HttpRequestException
    }
}
