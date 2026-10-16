using ngxPMIpUpdater.Models;
using ngxPMIpUpdater.Services;

namespace ngxPMIpUpdater.Tests;

public class ClientRulesTests
{
    private static IEnumerable<string> Render(IEnumerable<NpmClientEntry> rules) =>
        rules.Select(r => $"{r.Directive} {r.Address}");

    [Fact]
    public void Build_MergesAndDeduplicatesAsAllowRules()
    {
        var rules = ClientRules.Build(["1.1.1.1/32", "2.2.2.2/32"], ["10.0.0.0/8", "1.1.1.1/32"], []);

        Assert.Equal(["allow 1.1.1.1/32", "allow 2.2.2.2/32", "allow 10.0.0.0/8"], Render(rules!));
    }

    [Fact]
    public void Build_StaticBlocksOnly_AllowsThem()
    {
        var rules = ClientRules.Build([], ["10.0.0.0/8"], ["expired.example.com"]);

        Assert.Equal(["allow 10.0.0.0/8"], Render(rules!));
    }

    [Fact]
    public void Build_EverythingExpiredAndNoStaticBlocks_LocksDown()
    {
        var rules = ClientRules.Build([], [], ["home.example.com"]);

        Assert.Equal(["deny all"], Render(rules!));
    }

    [Fact]
    public void Build_NothingConfigured_WritesNothing()
    {
        Assert.Null(ClientRules.Build([], [], []));
    }

    [Fact]
    public void Build_NeverReturnsAnEmptyList()
    {
        // NPM renders an empty client list without "deny all;" – that would open the proxy
        Assert.All(
            new[] { ClientRules.Build([], [], []), ClientRules.Build([], [], ["x.example.com"]) },
            rules => Assert.True(rules is null || rules.Count > 0));
    }
}
