using Microsoft.Extensions.Options;
using ngxPMIpUpdater.Models;

namespace ngxPMIpUpdater.Services;

/// <summary>
/// Fails the startup on unusable configuration instead of letting the first update cycle throw an hour
/// into the container's life.
/// </summary>
public class AppSettingsValidator : IValidateOptions<AppSettings>
{
    public ValidateOptionsResult Validate(string? name, AppSettings settings)
    {
        var errors = new List<string>();

        if (settings.UsesNpm)
            ValidateNpm(settings.Npm, errors);

        if (settings.UsesCloudflare)
            ValidateCloudflare(settings.Cloudflare, errors);

        if (settings.IntervalMinutes <= 0)
            errors.Add("IntervalMinutes must be greater than 0.");

        if (settings.FallbackMaxAgeHours < 0)
            errors.Add("FallbackMaxAgeHours must be 0 (unlimited) or greater.");

        var notifications = settings.Notifications;
        if (!string.IsNullOrWhiteSpace(notifications.Url))
        {
            if (!Uri.TryCreate(notifications.Url, UriKind.Absolute, out var notifyUri)
                || (notifyUri.Scheme != Uri.UriSchemeHttp && notifyUri.Scheme != Uri.UriSchemeHttps))
                errors.Add("Notifications:Url is not an absolute http(s) URL."); // not echoed: may hold a secret

            if (!Notifier.SupportedTypes.Contains(notifications.Type, StringComparer.OrdinalIgnoreCase))
                errors.Add($"Notifications:Type '{notifications.Type}' is not supported. "
                           + $"Use one of: {string.Join(", ", Notifier.SupportedTypes)}.");
        }

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }

    private static void ValidateNpm(NpmSettings npm, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(npm.BaseUrl))
            errors.Add("Npm:BaseUrl is required, e.g. http://nginx-proxy-manager:81");
        else if (!Uri.TryCreate(npm.BaseUrl, UriKind.Absolute, out var uri)
                 || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            errors.Add($"Npm:BaseUrl '{npm.BaseUrl}' is not an absolute http(s) URL.");

        if (npm.AccessListId <= 0)
            errors.Add("Npm:AccessListId must be greater than 0 – it is the ID from the NPM URL "
                       + "/access-lists/<id>/edit");

        // At least one auth path. Having both is allowed and documented: the token takes precedence.
        var hasToken = !string.IsNullOrWhiteSpace(npm.ApiToken);
        var hasLogin = !string.IsNullOrWhiteSpace(npm.Email) && !string.IsNullOrWhiteSpace(npm.Password);

        if (!hasToken && !hasLogin)
            errors.Add("No NPM credentials configured. Set Npm:ApiToken (or Npm:ApiTokenFile), "
                       + "or both Npm:Email and Npm:Password.");
    }

    private static void ValidateCloudflare(CloudflareSettings cloudflare, List<string> errors)
    {
        // Both IDs end up in the request path – a strict format check also rules out path tricks
        if (cloudflare.AccountId is not { Length: 32 } accountId || !accountId.All(Uri.IsHexDigit))
            errors.Add("Cloudflare:AccountId must be the 32-character account ID from the dashboard URL "
                       + "dash.cloudflare.com/<account-id>/…");

        if (!Guid.TryParseExact(cloudflare.AccessGroupId ?? string.Empty, "D", out _))
            errors.Add("Cloudflare:AccessGroupId must be the UUID of the Access group "
                       + "(Zero Trust → Access → Access Groups → group → ID in the URL).");

        if (string.IsNullOrWhiteSpace(cloudflare.ApiToken))
            errors.Add("No Cloudflare API token configured. Set Cloudflare:ApiToken (or Cloudflare:ApiTokenFile).");
    }
}
