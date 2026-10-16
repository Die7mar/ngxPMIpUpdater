using ngxPMIpUpdater.Services;

namespace ngxPMIpUpdater.Tests;

public class ConfigListTests
{
    [Fact]
    public void Flatten_SplitsSeparatedValuesAndTrims()
    {
        var result = ConfigList.Flatten(["a.example.com, b.example.com;c.example.com\r\n", " d.example.com "]);

        Assert.Equal(["a.example.com", "b.example.com", "c.example.com", "d.example.com"], result);
    }

    [Fact]
    public void Flatten_DropsEmptyAndWhitespaceEntries()
    {
        var result = ConfigList.Flatten(["", "   ", ",,;", "a.example.com"]);

        Assert.Equal(["a.example.com"], result);
    }

    [Fact]
    public void Flatten_EmptyInput_ReturnsEmptyList()
    {
        Assert.Empty(ConfigList.Flatten([]));
    }
}
