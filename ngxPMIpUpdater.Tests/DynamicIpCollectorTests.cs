using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ngxPMIpUpdater.Models;
using ngxPMIpUpdater.Services;

namespace ngxPMIpUpdater.Tests;

public class DynamicIpCollectorTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly LastKnownIpStore _store;
    private readonly AppSettings _settings;

    public DynamicIpCollectorTests()
    {
        _settings = new AppSettings { StateFilePath = _dir.File("state.json"), FallbackMaxAgeHours = 72 };
        _store = new LastKnownIpStore(NullLogger<LastKnownIpStore>.Instance, Options.Create(_settings), _time);
    }

    public void Dispose() => _dir.Dispose();

    private DynamicIpCollector CreateCollector() =>
        new(NullLogger<DynamicIpCollector>.Instance, Options.Create(_settings), _store, _time);

    private static DomainResolution Resolved(string domain, params string[] cidrs) => new(domain, [.. cidrs], true);
    private static DomainResolution Failed(string domain) => new(domain, [], false);

    [Fact]
    public void TryCollect_AllResolved_ReturnsDeduplicatedIpsAndRemembersThem()
    {
        var ok = CreateCollector().TryCollect(
            [Resolved("a.example.com", "1.1.1.1/32"), Resolved("b.example.com", "1.1.1.1/32", "2.2.2.2/32")],
            out var ips, out _);

        Assert.True(ok);
        Assert.Equal(["1.1.1.1/32", "2.2.2.2/32"], ips);
        Assert.Equal(["1.1.1.1/32", "2.2.2.2/32"], _store.Get("b.example.com")!.Cidrs);
    }

    [Fact]
    public void TryCollect_NoDomains_SucceedsWithEmptyList()
    {
        Assert.True(CreateCollector().TryCollect([], out var ips, out _));
        Assert.Empty(ips);
    }

    [Fact]
    public void TryCollect_FailedWithRecentFallback_KeepsLastKnownIp()
    {
        _store.Set("home.example.com", ["1.2.3.4/32"]);
        _time.Advance(TimeSpan.FromHours(71));

        var ok = CreateCollector().TryCollect([Failed("home.example.com")], out var ips, out _);

        Assert.True(ok);
        Assert.Equal(["1.2.3.4/32"], ips);
    }

    [Fact]
    public void TryCollect_FailedWithExpiredFallback_DropsDomainButContinues()
    {
        _store.Set("home.example.com", ["1.2.3.4/32"]);
        _time.Advance(TimeSpan.FromHours(73));

        var ok = CreateCollector().TryCollect(
            [Failed("home.example.com"), Resolved("office.example.com", "5.6.7.8/32")],
            out var ips, out var expired);

        Assert.True(ok);
        Assert.Equal(["5.6.7.8/32"], ips);
        Assert.Equal(["home.example.com"], expired);
    }

    [Fact]
    public void TryCollect_FallbackAgeZero_NeverExpires()
    {
        _settings.FallbackMaxAgeHours = 0;
        _store.Set("home.example.com", ["1.2.3.4/32"]);
        _time.Advance(TimeSpan.FromDays(365));

        var ok = CreateCollector().TryCollect([Failed("home.example.com")], out var ips, out _);

        Assert.True(ok);
        Assert.Equal(["1.2.3.4/32"], ips);
    }

    [Fact]
    public void TryCollect_FailedWithoutFallback_Aborts()
    {
        var ok = CreateCollector().TryCollect(
            [Resolved("office.example.com", "5.6.7.8/32"), Failed("home.example.com")],
            out var ips, out _);

        Assert.False(ok);
        Assert.Empty(ips);
    }

    [Fact]
    public void TryCollect_SuccessfulResolution_ResetsExpiryClock()
    {
        _store.Set("home.example.com", ["1.2.3.4/32"]);
        _time.Advance(TimeSpan.FromHours(70));
        CreateCollector().TryCollect([Resolved("home.example.com", "1.2.3.4/32")], out _, out _);
        _time.Advance(TimeSpan.FromHours(70)); // 140 h since first, 70 h since last confirmation

        var ok = CreateCollector().TryCollect([Failed("home.example.com")], out var ips, out _);

        Assert.True(ok);
        Assert.Equal(["1.2.3.4/32"], ips);
    }
}
