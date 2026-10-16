using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ngxPMIpUpdater.Models;

namespace ngxPMIpUpdater.Services;

/// <summary>
/// Tracks whether update cycles keep succeeding and exposes that to Docker's HEALTHCHECK.
///
/// After every successful cycle a deadline ("healthy until") is written to a file. The health check
/// (<c>--healthcheck</c>) only compares that deadline with the clock – it needs no configuration, no HTTP
/// endpoint and no curl in the image. The grace period covers one missed cycle plus slack, so a single
/// failed attempt that is fixed by the 5-minute retry does not flip the container to unhealthy.
/// </summary>
public class HealthMonitor
{
    /// <summary>
    /// Lives in the temp directory on purpose: it must not survive a container re-creation (a stale
    /// deadline from a volume would report a broken new container as healthy), and /tmp is always writable.
    /// </summary>
    public static readonly string DefaultPath = Path.Combine(Path.GetTempPath(), "ngxPMIpUpdater.healthy-until");

    private readonly ILogger<HealthMonitor> _logger;
    private readonly TimeProvider _time;
    private readonly string _path;
    private readonly TimeSpan _grace;
    private DateTimeOffset _deadline;
    private DateTimeOffset? _lastSuccess;
    private bool _failing;
    private bool _writeWarningLogged;

    public HealthMonitor(ILogger<HealthMonitor> logger, IOptions<AppSettings> options, TimeProvider time)
        : this(logger, options, time, DefaultPath) { }

    public HealthMonitor(ILogger<HealthMonitor> logger, IOptions<AppSettings> options, TimeProvider time, string path)
    {
        _logger = logger;
        _time   = time;
        _path   = path;
        _grace  = GracePeriod(options.Value.IntervalMinutes);

        // A restarted container keeps its /tmp – drop the previous run's deadline
        TryDelete();
        _deadline = _time.GetUtcNow() + _grace;
    }

    /// <summary>How long after the last success the service still counts as healthy.</summary>
    public static TimeSpan GracePeriod(int intervalMinutes) => TimeSpan.FromMinutes(2 * intervalMinutes + 5);

    public DateTimeOffset? LastSuccess => _lastSuccess;

    /// <summary>Records a successful cycle. Returns true if this ends a reported failure phase.</summary>
    public bool RecordSuccess()
    {
        var now = _time.GetUtcNow();
        _lastSuccess = now;
        _deadline = now + _grace;
        Write(_deadline);

        var recovered = _failing;
        _failing = false;
        return recovered;
    }

    /// <summary>
    /// Records a failed cycle. Returns true exactly once per failure phase: when the grace period has run out,
    /// which is the same moment the health check starts failing.
    /// </summary>
    public bool RecordFailure()
    {
        if (_failing || _time.GetUtcNow() <= _deadline)
            return false;

        _failing = true;
        return true;
    }

    /// <summary>Implements <c>--healthcheck</c>. Returns the process exit code (0 = healthy).</summary>
    public static int Check(string path, DateTimeOffset now, TextWriter output)
    {
        if (!File.Exists(path))
        {
            output.WriteLine("unhealthy: no successful update cycle yet");
            return 1;
        }

        if (!DateTimeOffset.TryParse(File.ReadAllText(path).Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var deadline))
        {
            output.WriteLine($"unhealthy: cannot read {path}");
            return 1;
        }

        if (now > deadline)
        {
            output.WriteLine($"unhealthy: no successful update cycle in time, deadline {deadline:u} has passed");
            return 1;
        }

        output.WriteLine($"healthy until {deadline:u}");
        return 0;
    }

    private void Write(DateTimeOffset deadline)
    {
        try
        {
            // Write-then-move, so the health check never reads a half-written file
            var temp = _path + ".tmp";
            File.WriteAllText(temp, deadline.ToString("O", CultureInfo.InvariantCulture));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            if (_writeWarningLogged)
                return;

            _writeWarningLogged = true;
            _logger.LogWarning(ex, "Could not write health file {Path}; the Docker health check will report unhealthy.",
                _path);
        }
    }

    private void TryDelete()
    {
        try
        {
            File.Delete(_path);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not delete stale health file {Path}.", _path);
        }
    }
}
