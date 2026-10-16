using ngxPMIpUpdater.Models;
using ngxPMIpUpdater.Services;

namespace ngxPMIpUpdater.Tests;

public class ChangeNotificationTests
{
    private static List<NpmClientEntry> Allow(params string[] addresses) =>
        [.. addresses.Select(a => new NpmClientEntry { Address = a, Directive = "allow" })];

    [Fact]
    public void Describe_RegularChange_CountsAddedAndRemovedWithoutAddresses()
    {
        var message = ChangeNotification.Describe("Home",
            Allow("1.1.1.1/32", "10.0.0.0/8"), Allow("2.2.2.2/32", "10.0.0.0/8"), [], notifyOnChange: true);

        Assert.NotNull(message);
        Assert.Equal(NotificationLevel.Info, message.Level);
        Assert.Contains("1 rule(s) added, 1 removed, 2 total", message.Message);
        Assert.DoesNotContain("2.2.2.2", message.Message);
    }

    [Fact]
    public void Describe_RegularChange_NotifyOnChangeOff_ReturnsNull()
    {
        Assert.Null(ChangeNotification.Describe("Home", Allow("1.1.1.1/32"), Allow("2.2.2.2/32"), [],
            notifyOnChange: false));
    }

    [Fact]
    public void Describe_LockDown_IsCriticalEvenWithNotifyOnChangeOff()
    {
        var message = ChangeNotification.Describe("Home",
            Allow("1.1.1.1/32"), [ClientRules.DenyAll], ["home.example.com"], notifyOnChange: false);

        Assert.NotNull(message);
        Assert.Equal(NotificationLevel.Critical, message.Level);
        Assert.Contains("home.example.com", message.Message);
    }

    [Fact]
    public void Describe_LeavingLockDown_ReportsRestoredEvenWithNotifyOnChangeOff()
    {
        var message = ChangeNotification.Describe("Home",
            [ClientRules.DenyAll], Allow("1.1.1.1/32"), [], notifyOnChange: false);

        Assert.NotNull(message);
        Assert.Equal("Access restored", message.Title);
    }
}
