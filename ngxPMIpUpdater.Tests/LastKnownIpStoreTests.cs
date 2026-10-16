using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ngxPMIpUpdater.Models;
using ngxPMIpUpdater.Services;

namespace ngxPMIpUpdater.Tests;

public class LastKnownIpStoreTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _dir.Dispose();

    private LastKnownIpStore CreateStore(string path) => new(
        NullLogger<LastKnownIpStore>.Instance,
        Options.Create(new AppSettings { StateFilePath = path }),
        _time);

    [Fact]
    public void Get_UnknownDomain_ReturnsNull()
    {
        Assert.Null(CreateStore(_dir.File("state.json")).Get("unknown.example.com"));
    }

    [Fact]
    public void Set_PersistsAcrossInstances()
    {
        var path = _dir.File("state.json");
        CreateStore(path).Set("home.example.com", ["1.2.3.4/32"]);

        var entry = CreateStore(path).Get("home.example.com");

        Assert.NotNull(entry);
        Assert.Equal(["1.2.3.4/32"], entry.Cidrs);
        Assert.Equal(_time.GetUtcNow(), entry.ResolvedAt);
    }

    [Fact]
    public void Set_SameIps_StillRefreshesTimestamp()
    {
        var store = CreateStore(_dir.File("state.json"));
        store.Set("home.example.com", ["1.2.3.4/32"]);

        _time.Advance(TimeSpan.FromHours(5));
        store.Set("home.example.com", ["1.2.3.4/32"]);

        Assert.Equal(_time.GetUtcNow(), store.Get("home.example.com")!.ResolvedAt);
    }

    [Fact]
    public void Set_CreatesMissingDirectory()
    {
        var path = Path.Combine(_dir.Path, "nested", "dir", "state.json");

        CreateStore(path).Set("home.example.com", ["1.2.3.4/32"]);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Load_LegacyFormatWithoutTimestamp_IsTreatedAsResolvedNow()
    {
        var path = _dir.File("state.json");
        File.WriteAllText(path, """{"home.example.com":["1.2.3.4/32","5.6.7.8/32"]}""");

        var entry = CreateStore(path).Get("home.example.com");

        Assert.NotNull(entry);
        Assert.Equal(["1.2.3.4/32", "5.6.7.8/32"], entry.Cidrs);
        Assert.Equal(_time.GetUtcNow(), entry.ResolvedAt);
    }

    [Fact]
    public void Load_CorruptFile_StartsEmpty()
    {
        var path = _dir.File("state.json");
        File.WriteAllText(path, "{ not json");

        Assert.Null(CreateStore(path).Get("home.example.com"));
    }

    [Fact]
    public void Set_UnwritablePath_KeepsWorkingInMemory()
    {
        // A file where a directory is expected makes CreateDirectory fail
        var blocker = _dir.File("blocker");
        File.WriteAllText(blocker, "");
        var store = CreateStore(Path.Combine(blocker, "state.json"));

        store.Set("home.example.com", ["1.2.3.4/32"]);

        Assert.Equal(["1.2.3.4/32"], store.Get("home.example.com")!.Cidrs);
    }
}
