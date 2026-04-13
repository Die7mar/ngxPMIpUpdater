namespace ngxPMIpUpdater.Models;

public class AppSettings
{
    public NpmSettings Npm { get; set; } = new();
    public List<string> Domains { get; set; } = [];
    /// <summary>
    /// Static IP addresses or CIDR blocks that are always allowed (e.g. "192.168.1.0/24", "10.0.0.1/32").
    /// These are merged with dynamically resolved domain IPs.
    /// </summary>
    public List<string> StaticIpBlocks { get; set; } = [];
    public int IntervalMinutes { get; set; } = 60;
}

public class NpmSettings
{
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Long-lived API token (NPM v2.13+). Create it in the NPM UI under Access → API Tokens.
    /// If set, Email/Password are ignored.
    /// </summary>
    public string? ApiToken { get; set; }

    /// <summary>Email for login-based auth (NPM older than v2.13).</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Password for login-based auth (NPM older than v2.13).</summary>
    public string Password { get; set; } = string.Empty;

    public int AccessListId { get; set; }
}
