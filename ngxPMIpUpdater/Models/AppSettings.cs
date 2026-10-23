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

    /// <summary>
    /// Allows "0.0.0.0/0" / "::/0" in <see cref="StaticIpBlocks"/>, which grants access to the entire
    /// internet. Off by default – such an entry is almost always a configuration mistake.
    /// </summary>
    public bool AllowOpenBlocks { get; set; }

    /// <summary>
    /// Where the last successfully resolved IP per domain is persisted, so a container restart does not
    /// lose the protection against writing a reduced access list during a DNS outage.
    /// </summary>
    public string StateFilePath { get; set; } = "/data/last-known.json";

    /// <summary>
    /// How long (in hours) the last known IP of a domain that no longer resolves is kept as an allow rule.
    /// After that it is removed: an old DynDNS address may have been reassigned to someone else.
    /// 0 keeps it indefinitely (not recommended).
    /// </summary>
    public int FallbackMaxAgeHours { get; set; } = 72;

    /// <summary>
    /// Also turn AAAA records into allow rules (as /128). Off by default: many home connections rotate
    /// their IPv6 interface ID via privacy extensions, which makes a /128 rule go stale quickly.
    /// </summary>
    public bool ResolveIPv6 { get; set; }

    public NotificationSettings Notifications { get; set; } = new();

    public CloudflareSettings Cloudflare { get; set; } = new();

    /// <summary>
    /// NPM is updated when its URL is set. Without Cloudflare it is the only target and therefore required –
    /// validation then reports the missing NPM settings instead of a service that silently does nothing.
    /// </summary>
    public bool UsesNpm => !string.IsNullOrWhiteSpace(Npm.BaseUrl) || !UsesCloudflare;

    public bool UsesCloudflare => Cloudflare.IsConfigured;
}

/// <summary>
/// Optional second target: a Cloudflare Zero Trust Access group for services behind a Cloudflare Tunnel.
/// Behind a tunnel NPM only sees cloudflared's address, so the IP check has to happen at Cloudflare.
/// </summary>
public class CloudflareSettings
{
    /// <summary>Account ID (32 hex characters), shown in the dashboard URL and on the account overview.</summary>
    public string? AccountId { get; set; }

    /// <summary>ID (UUID) of the Access group whose IP rules are kept in sync.</summary>
    public string? AccessGroupId { get; set; }

    /// <summary>
    /// API token with "Account → Access: Organizations, Identity Providers, and Groups → Edit".
    /// </summary>
    public string? ApiToken { get; set; }

    /// <summary>Path to a file holding <see cref="ApiToken"/>, e.g. a Docker secret. Overrides it when set.</summary>
    public string? ApiTokenFile { get; set; }

    /// <summary>Any Cloudflare setting present means "use Cloudflare" – validation then demands the rest.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AccountId) || !string.IsNullOrWhiteSpace(AccessGroupId)
        || !string.IsNullOrWhiteSpace(ApiToken);
}

/// <summary>
/// Optional push notifications. Without <see cref="Url"/> nothing is sent.
/// </summary>
public class NotificationSettings
{
    /// <summary>
    /// Target URL, e.g. "https://ntfy.sh/my-topic", "https://gotify.example.com/message" or any endpoint
    /// that accepts a JSON POST. For Discord this is the webhook URL, which contains its secret.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Path to a file holding <see cref="Url"/>. Meant for Discord, whose webhook URL is the credential.
    /// Overrides <see cref="Url"/> when set.
    /// </summary>
    public string? UrlFile { get; set; }

    /// <summary>"ntfy" (default), "gotify", "discord" or "json".</summary>
    public string Type { get; set; } = "ntfy";

    /// <summary>
    /// Access token: sent as "Authorization: Bearer" for ntfy/json and as "X-Gotify-Key" for Gotify (the
    /// app token).
    /// </summary>
    public string? Token { get; set; }

    /// <summary>Path to a file holding <see cref="Token"/>, e.g. a Docker secret. Overrides it when set.</summary>
    public string? TokenFile { get; set; }

    /// <summary>
    /// Also notify on every regular change of the access list (new DynDNS IP). Lockdown, failures and
    /// recovery are always reported.
    /// </summary>
    public bool NotifyOnChange { get; set; } = true;
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

    /// <summary>
    /// Path to a file holding the API token, e.g. a Docker secret at /run/secrets/npm_api_token.
    /// Preferred over <see cref="ApiToken"/>: environment variables are readable through
    /// "docker inspect" and /proc/&lt;pid&gt;/environ. Overrides <see cref="ApiToken"/> when set.
    /// </summary>
    public string? ApiTokenFile { get; set; }

    /// <summary>Path to a file holding the login password. Overrides <see cref="Password"/> when set.</summary>
    public string? PasswordFile { get; set; }

    public int AccessListId { get; set; }
}
