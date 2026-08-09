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
        var npm = settings.Npm;

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

        if (settings.IntervalMinutes <= 0)
            errors.Add("IntervalMinutes must be greater than 0.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
