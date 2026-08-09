namespace ngxPMIpUpdater.Services;

/// <summary>
/// Flattens list-style settings so they can be written either way:
/// <list type="bullet">
/// <item>as a JSON array in appsettings.json</item>
/// <item>as one environment variable holding a separated list
/// (<c>AppSettings__Domains__0=a.example.com,b.example.com</c>)</item>
/// <item>as individually indexed environment variables (the old
/// <c>AppSettings__Domains__0</c> / <c>__1</c> / <c>__2</c> style)</item>
/// </list>
/// All three end up as the same flat list, so nobody has to count index slots.
/// </summary>
public static class ConfigList
{
    private static readonly char[] Separators = [',', ';', '\n', '\r'];

    public static List<string> Flatten(IEnumerable<string> values) =>
    [
        .. values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .SelectMany(v => v.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            .Select(v => v.Trim())
            .Where(v => v.Length > 0)
    ];
}
