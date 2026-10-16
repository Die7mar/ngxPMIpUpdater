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
}
