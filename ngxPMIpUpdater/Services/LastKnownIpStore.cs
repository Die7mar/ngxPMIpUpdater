using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ngxPMIpUpdater.Models;

namespace ngxPMIpUpdater.Services;

/// <summary>
/// Remembers the last successfully resolved addresses per domain, so a DNS outage cannot cause the access
/// list to be rewritten without that domain – which would lock the operator out until DNS recovers.
///
/// Persistence is best-effort: if the state file is not writable (e.g. no volume mounted) the store keeps
/// working in memory and says so once. Losing the file is not worth failing a cycle over.
/// </summary>
public class LastKnownIpStore
{
    private readonly ILogger<LastKnownIpStore> _logger;
    private readonly string _path;
    private readonly Dictionary<string, List<string>> _entries;
    private bool _persistenceWarningLogged;

    public LastKnownIpStore(ILogger<LastKnownIpStore> logger, IOptions<AppSettings> options)
    {
        _logger  = logger;
        _path    = options.Value.StateFilePath;
        _entries = Load();
    }

    public IReadOnlyList<string>? Get(string domain) =>
        _entries.TryGetValue(domain, out var cidrs) ? cidrs : null;

    public void Set(string domain, List<string> cidrs)
    {
        if (_entries.TryGetValue(domain, out var existing) && existing.SequenceEqual(cidrs))
            return;

        _entries[domain] = cidrs;
        Save();
    }

    private Dictionary<string, List<string>> Load()
    {
        if (string.IsNullOrWhiteSpace(_path) || !File.Exists(_path))
            return [];

        try
        {
            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json);
            if (loaded is { Count: > 0 })
                _logger.LogInformation("Loaded last known IPs for {Count} domain(s) from {Path}.",
                    loaded.Count, _path);
            return loaded ?? [];
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
