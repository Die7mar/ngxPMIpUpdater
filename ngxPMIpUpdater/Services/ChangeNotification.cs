using ngxPMIpUpdater.Models;

namespace ngxPMIpUpdater.Services;

public record NotificationMessage(NotificationLevel Level, string Title, string Message);

/// <summary>
/// Turns a written access-list change into a notification. Counts only, no addresses: home IPs are personal
/// data and may end up on a third-party push server (e.g. ntfy.sh).
/// </summary>
public static class ChangeNotification
{
    /// <summary>Returns null when the change is not worth a notification.</summary>
    public static NotificationMessage? Describe(string listName, IReadOnlyList<NpmClientEntry> before,
        IReadOnlyList<NpmClientEntry> after, IReadOnlyCollection<string> expiredDomains, bool notifyOnChange)
    {
        if (IsDenyAll(after))
            return new NotificationMessage(NotificationLevel.Critical, "Access list locked down",
                $"Access list '{listName}' was set to 'deny all': the last known IP(s) of "
                + $"{string.Join(", ", expiredDomains)} expired and no static blocks are configured. "
                + "Basic-auth users can still log in. Access is restored automatically once DNS resolves again.");

        if (IsDenyAll(before))
            return new NotificationMessage(NotificationLevel.Info, "Access restored",
                $"Access list '{listName}' allows {after.Count} address(es) again.");

        if (!notifyOnChange)
            return null;

        var beforeSet = before.Select(Key).ToHashSet();
        var afterSet  = after.Select(Key).ToHashSet();

        return new NotificationMessage(NotificationLevel.Info, "Access list updated",
            $"Access list '{listName}': {afterSet.Except(beforeSet).Count()} rule(s) added, "
            + $"{beforeSet.Except(afterSet).Count()} removed, {after.Count} total.");
    }

    private static bool IsDenyAll(IReadOnlyList<NpmClientEntry> clients) =>
        clients is [{ Directive: "deny", Address: "all" }];

    private static string Key(NpmClientEntry c) => $"{c.Directive} {c.Address}";
}
