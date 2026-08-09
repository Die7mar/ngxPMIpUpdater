using ngxPMIpUpdater.Models;

namespace ngxPMIpUpdater.Services;

/// <summary>
/// Resolves the "*File" settings into their corresponding value, following the Docker-secrets convention
/// (<c>/run/secrets/...</c>). Preferred over passing secrets as environment variables, which are readable
/// via <c>docker inspect</c> and <c>/proc/&lt;pid&gt;/environ</c>.
/// </summary>
public static class SecretFiles
{
    public static void Resolve(AppSettings settings)
    {
        if (Read(settings.Npm.ApiTokenFile, "Npm:ApiTokenFile") is { } token)
            settings.Npm.ApiToken = token;

        if (Read(settings.Npm.PasswordFile, "Npm:PasswordFile") is { } password)
            settings.Npm.Password = password;
    }

    /// <summary>
    /// Reads a secret file, or returns null if none was configured. Throws when the path is set but
    /// unusable – silently ignoring it would let the service fall back to a different (or no) auth path,
    /// which is exactly the kind of surprise that must not happen with credentials.
    /// </summary>
    private static string? Read(string? path, string settingName)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"{settingName} points to '{path}', which does not exist. Refusing to start with a missing secret.");

        var value = File.ReadAllText(path).Trim();
        if (value.Length == 0)
            throw new InvalidOperationException($"{settingName} points to '{path}', which is empty.");

        return value;
    }
}
