using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ngxPMIpUpdater.Models;
using ngxPMIpUpdater.Services;

namespace ngxPMIpUpdater.Tests;

public class HealthMonitorTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    // 60 min interval → 2 × 60 + 5 = 125 min grace
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(125);

    public void Dispose() => _dir.Dispose();

    private string HealthFile => _dir.File("healthy-until");

    private HealthMonitor CreateMonitor() => new(
        NullLogger<HealthMonitor>.Instance,
        Options.Create(new AppSettings { IntervalMinutes = 60 }),
        _time,
        HealthFile);

    private int Check() => HealthMonitor.Check(HealthFile, _time.GetUtcNow(), TextWriter.Null);

    [Fact]
    public void Check_BeforeFirstSuccess_IsUnhealthy()
    {
        CreateMonitor();

        Assert.Equal(1, Check());
    }

    [Fact]
    public void Check_AfterSuccess_IsHealthyUntilGraceRunsOut()
    {
        CreateMonitor().RecordSuccess();

        _time.Advance(Grace);
        Assert.Equal(0, Check());

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, Check());
    }

    [Fact]
    public void Constructor_RemovesStaleDeadlineFromPreviousRun()
    {
        CreateMonitor().RecordSuccess();

        CreateMonitor();

        Assert.False(File.Exists(HealthFile));
    }

    [Fact]
    public void Check_GarbageFile_IsUnhealthy()
    {
        File.WriteAllText(HealthFile, "not a date");

        Assert.Equal(1, Check());
    }

    [Fact]
    public void RecordFailure_WithinGrace_DoesNotReport()
    {
        var monitor = CreateMonitor();
        monitor.RecordSuccess();

        _time.Advance(TimeSpan.FromMinutes(60));

        Assert.False(monitor.RecordFailure());
    }

    [Fact]
    public void RecordFailure_AfterGrace_ReportsOncePerFailurePhase()
    {
        var monitor = CreateMonitor();
        monitor.RecordSuccess();
        _time.Advance(Grace + TimeSpan.FromMinutes(1));

        Assert.True(monitor.RecordFailure());
        Assert.False(monitor.RecordFailure());

        Assert.True(monitor.RecordSuccess(), "first success after a reported failure is a recovery");
        Assert.False(monitor.RecordSuccess());
    }

    [Fact]
    public void RecordFailure_NeverSucceededSinceStartup_ReportsAfterGrace()
    {
        var monitor = CreateMonitor();

        Assert.False(monitor.RecordFailure());

        _time.Advance(Grace + TimeSpan.FromMinutes(1));
        Assert.True(monitor.RecordFailure());
        Assert.Null(monitor.LastSuccess);
    }

    [Fact]
    public void RecordSuccess_WithoutReportedFailure_IsNoRecovery()
    {
        var monitor = CreateMonitor();
        monitor.RecordFailure(); // within grace – not reported

        Assert.False(monitor.RecordSuccess());
    }
}
