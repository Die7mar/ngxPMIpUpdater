using ngxPMIpUpdater.Models;
using ngxPMIpUpdater.Services;

namespace ngxPMIpUpdater.Tests;

public class AppSettingsValidatorTests
{
    private static AppSettings Valid() => new()
    {
        Npm = new NpmSettings { BaseUrl = "http://nginx-proxy-manager:81", ApiToken = "token", AccessListId = 1 }
    };

    private static IEnumerable<string> Errors(AppSettings settings) =>
        new AppSettingsValidator().Validate(null, settings).Failures ?? [];

    [Fact]
    public void Validate_ValidSettings_Succeeds()
    {
        Assert.True(new AppSettingsValidator().Validate(null, Valid()).Succeeded);
    }

    [Fact]
    public void Validate_EmailAndPasswordWithoutToken_Succeeds()
    {
        var settings = Valid();
        settings.Npm.ApiToken = null;
        settings.Npm.Email = "admin@example.com";
        settings.Npm.Password = "secret";

        Assert.Empty(Errors(settings));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nginx-proxy-manager:81")]
    [InlineData("ftp://nginx-proxy-manager")]
    public void Validate_InvalidBaseUrl_Fails(string baseUrl)
    {
        var settings = Valid();
        settings.Npm.BaseUrl = baseUrl;

        Assert.Contains(Errors(settings), e => e.Contains("BaseUrl"));
    }

    [Fact]
    public void Validate_MissingAccessListId_Fails()
    {
        var settings = Valid();
        settings.Npm.AccessListId = 0;

        Assert.Contains(Errors(settings), e => e.Contains("AccessListId"));
    }

    [Fact]
    public void Validate_NoCredentials_Fails()
    {
        var settings = Valid();
        settings.Npm.ApiToken = null;
        settings.Npm.Email = "admin@example.com"; // password missing

        Assert.Contains(Errors(settings), e => e.Contains("credentials"));
    }

    [Fact]
    public void Validate_NonPositiveInterval_Fails()
    {
        var settings = Valid();
        settings.IntervalMinutes = 0;

        Assert.Contains(Errors(settings), e => e.Contains("IntervalMinutes"));
    }

    [Fact]
    public void Validate_NegativeFallbackAge_Fails()
    {
        var settings = Valid();
        settings.FallbackMaxAgeHours = -1;

        Assert.Contains(Errors(settings), e => e.Contains("FallbackMaxAgeHours"));
    }

    [Theory]
    [InlineData("ntfy.sh/topic", "ntfy")]
    [InlineData("https://ntfy.sh/topic", "telegram")]
    public void Validate_InvalidNotifications_Fails(string url, string type)
    {
        var settings = Valid();
        settings.Notifications = new NotificationSettings { Url = url, Type = type };

        Assert.Contains(Errors(settings), e => e.Contains("Notifications"));
    }

    [Fact]
    public void Validate_NotificationTypeIsCaseInsensitive()
    {
        var settings = Valid();
        settings.Notifications = new NotificationSettings { Url = "https://gotify.example.com/message", Type = "Gotify" };

        Assert.Empty(Errors(settings));
    }

    [Fact]
    public void Validate_ReportsAllErrorsAtOnce()
    {
        Assert.Equal(3, Errors(new AppSettings()).Count()); // BaseUrl, AccessListId, credentials
    }

    private static CloudflareSettings ValidCloudflare() => new()
    {
        AccountId = "0123456789abcdef0123456789abcdef",
        AccessGroupId = "8d3f1c2a-5b6e-4f70-9a81-2c3d4e5f6a7b",
        ApiToken = "cf-token"
    };

    [Fact]
    public void Validate_CloudflareOnly_DoesNotRequireNpm()
    {
        var settings = new AppSettings { Cloudflare = ValidCloudflare() };

        Assert.False(settings.UsesNpm);
        Assert.Empty(Errors(settings));
    }

    [Fact]
    public void Validate_NpmAndCloudflare_ValidatesBoth()
    {
        var settings = Valid();
        settings.Cloudflare = ValidCloudflare();
        settings.Npm.AccessListId = 0;

        Assert.True(settings.UsesNpm);
        Assert.True(settings.UsesCloudflare);
        Assert.Contains("AccessListId", Assert.Single(Errors(settings)));
    }

    [Theory]
    [InlineData("0123456789abcdef", null, null, "AccountId")]                         // too short
    [InlineData("0123456789abcdef0123456789abcdeg", null, null, "AccountId")]         // not hex
    [InlineData(null, "../../user/tokens", null, "AccessGroupId")]                   // not a UUID
    [InlineData(null, null, "", "Cloudflare API token")]
    public void Validate_InvalidCloudflare_Fails(string? accountId, string? groupId, string? token, string expected)
    {
        var cloudflare = ValidCloudflare();
        if (accountId != null) cloudflare.AccountId = accountId;
        if (groupId != null) cloudflare.AccessGroupId = groupId;
        if (token != null) cloudflare.ApiToken = token;

        var errors = Errors(new AppSettings { Cloudflare = cloudflare }).ToList();

        Assert.Contains(errors, e => e.Contains(expected));
        Assert.DoesNotContain(errors, e => e.Contains("Npm"));
    }

    [Fact]
    public void Validate_PartialCloudflareSetup_IsReportedInsteadOfIgnored()
    {
        var settings = Valid();
        settings.Cloudflare = new CloudflareSettings { AccountId = "0123456789abcdef0123456789abcdef" };

        Assert.Equal(2, Errors(settings).Count()); // group ID and token
    }
}
