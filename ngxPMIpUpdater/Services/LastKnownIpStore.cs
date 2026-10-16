using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ngxPMIpUpdater.Models;

namespace ngxPMIpUpdater.Services;

/// <summary>The last successful resolution of a domain.</summary>
public record LastKnownEntry(
    [property: JsonPropertyName("cidrs")] List<string> Cidrs,
    [property: JsonPropertyName("resolvedAt")] DateTimeOffset ResolvedAt);

/// <summary>
/// Remembers the last successfully resolved addresses per domain, so a DNS outage cannot cause the access
/// list to be rewritten without that domain – which would lock the operator out until DNS recovers.
/// Each entry carries the time of its last successful resolution, so the caller can stop trusting an
/// address that has not been confirmed by DNS for too long.
///
/// Persistence is best-effort: if the state file is not writable (e.g. no volume mounted) the store keeps
/// working in memory and says so once. Losing the file is not worth failing a cycle over.
/// </summary>
public class LastKnownIpStore
{
    private readonly ILogger<LastKnownIpStore> _logger;
    private readonly TimeProvider _time;
    private readonly string _path;
    private readonly Dictionary<string, LastKnownEntry> _entries;
    private bool _persistenceWarningLogged;

    public LastKnownIpStore(ILogger<LastKnownIpStore> logger, IOptions<AppSettings> options, TimeProvider time)
    {
        _logger  = logger;
        _time    = time;
        _path    = options.Value.StateFilePath;
        _entries = Load();
    }

    public LastKnownEntry? Get(string domain) =>
        _entries.TryGetValue(domain, out var entry) ? entry : null;

    /// <summary>Records a successful resolution. Always refreshes the timestamp, even if the IPs are unchanged.</summary>
    public void Set(string domain, List<string> cidrs)
    {
        _entries[domain] = new LastKnownEntry(cidrs, _time.GetUtcNow());
        Save();
    }

    private Dictionary<string, LastKnownEntry> Load()
    {
        if (string.IsNullOrWhiteSpace(_path) || !File.Exists(_path))
            return [];

        try
        {
            var json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(_path)) ?? [];
            var entries = new Dictionary<string, LastKnownEntry>();
            var legacyCount = 0;

            foreach (var (domain, value) in json)
            {
                if (value.ValueKind == JsonValueKind.Array)
                {
                    // Pre-timestamp format ({"domain": ["1.2.3.4/32"]}). The age is unknown, so start the
                    // clock now rather than expiring the entry immediately and aborting the first cycle.
                    entries[domain] = new LastKnownEntry(value.Deserialize<List<string>>() ?? [], _time.GetUtcNow());
                    legacyCount++;
                }
                else if (value.Deserialize<LastKnownEntry>() is { } entry)
                {
                    entries[domain] = entry;
                }
            }

            if (entries.Count > 0)
                _logger.LogInformation("Loaded last known IPs for {Count} domain(s) from {Path}.",
                    entries.Count, _path);
            if (legacyCount > 0)
                _logger.LogInformation(
                    "{Count} entry/entries in {Path} have no timestamp (old format); treating them as resolved now.",
                    legacyCount, _path);

            return entries;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read {Path}; starting without last known IPs.", _path);
            return [];
        }
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(_path))
            return;

        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(_path, JsonSerializer.Serialize(_entries));
        }
        catch (Exception ex)
        {
            if (_persistenceWarningLogged)
                return;

            _persistenceWarningLogged = true;
            _logger.LogWarning(ex,
                "Could not persist last known IPs to {Path}. Continuing in memory only – after a restart the "
                + "lockout protection has no history to fall back on. Mount a writable volume to fix this.",
                _path);
        }
    }
}
