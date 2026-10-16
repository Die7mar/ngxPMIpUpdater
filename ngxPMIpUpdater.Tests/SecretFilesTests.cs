using ngxPMIpUpdater.Models;
using ngxPMIpUpdater.Services;

namespace ngxPMIpUpdater.Tests;

public class SecretFilesTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Resolve_ReadsAndTrimsSecretFiles()
    {
        File.WriteAllText(_dir.File("token"), "file-token\n");
        File.WriteAllText(_dir.File("password"), "  file-password  ");
        var settings = new AppSettings
        {
            Npm = new NpmSettings
            {
                ApiToken = "env-token", ApiTokenFile = _dir.File("token"),
                Password = "env-password", PasswordFile = _dir.File("password")
            }
        };

        SecretFiles.Resolve(settings);

        Assert.Equal("file-token", settings.Npm.ApiToken);
        Assert.Equal("file-password", settings.Npm.Password);
    }

    [Fact]
    public void Resolve_WithoutFileSettings_LeavesValuesUntouched()
    {
        var settings = new AppSettings { Npm = new NpmSettings { ApiToken = "env-token", Password = "env-password" } };

        SecretFiles.Resolve(settings);

        Assert.Equal("env-token", settings.Npm.ApiToken);
        Assert.Equal("env-password", settings.Npm.Password);
    }

    [Fact]
    public void Resolve_MissingFile_Throws()
    {
        var settings = new AppSettings { Npm = new NpmSettings { ApiTokenFile = _dir.File("does-not-exist") } };

        var ex = Assert.Throws<InvalidOperationException>(() => SecretFiles.Resolve(settings));
        Assert.Contains("Npm:ApiTokenFile", ex.Message);
    }

    [Fact]
    public void Resolve_EmptyFile_Throws()
    {
        File.WriteAllText(_dir.File("password"), " \n");
        var settings = new AppSettings { Npm = new NpmSettings { PasswordFile = _dir.File("password") } };

        var ex = Assert.Throws<InvalidOperationException>(() => SecretFiles.Resolve(settings));
        Assert.Contains("empty", ex.Message);
    }
}
