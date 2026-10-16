using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ngxPMIpUpdater.Models;

namespace ngxPMIpUpdater.Services;

public enum NotificationLevel { Info, Warning, Critical }

/// <summary>
/// Sends push notifications to ntfy, Gotify, a Discord webhook or a generic JSON webhook.
///
/// Strictly best-effort: a notification failure is logged and swallowed. The access list must never be left
/// in a worse state because a notification endpoint was down.
/// </summary>
public class Notifier(HttpClient httpClient, IOptions<AppSettings> options, ILogger<Notifier> logger)
{
    public static readonly string[] SupportedTypes = ["ntfy", "gotify", "discord", "json"];

    private readonly NotificationSettings _settings = options.Value.Notifications;

    public bool Enabled => !string.IsNullOrWhiteSpace(_settings.Url);

    public bool NotifyOnChange => _settings.NotifyOnChange;

    public async Task SendAsync(NotificationLevel level, string title, string message,
        CancellationToken cancellationToken = default)
    {
        if (!Enabled)
            return;

        try
        {
            using var request = BuildRequest(level, title, message);
            using var response = await httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
                logger.LogWarning("Notification '{Title}' was rejected: HTTP {Status}.", title, (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not send notification '{Title}'.", title);
        }
    }

    private HttpRequestMessage BuildRequest(NotificationLevel level, string title, string message)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _settings.Url);
        var token = _settings.Token;

        switch (_settings.Type.ToLowerInvariant())
        {
            case "gotify":
                request.Content = JsonContent.Create(new { title, message, priority = GotifyPriority(level) });
                if (!string.IsNullOrWhiteSpace(token))
                    request.Headers.Add("X-Gotify-Key", token);
                break;

            case "discord": // the webhook URL itself carries the secret, so no token header
                request.Content = JsonContent.Create(new
                {
                    username = "ngxPMIpUpdater",
                    embeds = new[] { new { title, description = message, color = DiscordColor(level) } }
                });
                break;

            case "json":
                request.Content = JsonContent.Create(new
                {
                    title, message, level = level.ToString().ToLowerInvariant(), source = "ngxPMIpUpdater"
                });
                AddBearer(request, token);
                break;

            default: // ntfy: plain-text body, metadata in headers (header values must stay ASCII)
                request.Content = new StringContent(message);
                request.Headers.Add("Title", title);
                request.Headers.Add("Priority", NtfyPriority(level));
                request.Headers.Add("Tags", NtfyTag(level));
                AddBearer(request, token);
                break;
        }

        return request;
    }

    private static void AddBearer(HttpRequestMessage request, string? token)
    {
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static string NtfyPriority(NotificationLevel level) => level switch
    {
        NotificationLevel.Critical => "5",
        NotificationLevel.Warning  => "4",
        _                          => "3"
    };

    private static string NtfyTag(NotificationLevel level) => level switch
    {
        NotificationLevel.Critical => "rotating_light",
        NotificationLevel.Warning  => "warning",
        _                          => "white_check_mark"
    };

    private static int DiscordColor(NotificationLevel level) => level switch
    {
        NotificationLevel.Critical => 0xE74C3C, // red
        NotificationLevel.Warning  => 0xE67E22, // orange
        _                          => 0x2ECC71  // green
    };

    private static int GotifyPriority(NotificationLevel level) => level switch
    {
        NotificationLevel.Critical => 10,
        NotificationLevel.Warning  => 8,
        _                          => 5
    };
}
