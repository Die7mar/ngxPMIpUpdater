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
    /// <param name="target">What was written, e.g. "Access list 'Home'".</param>
    /// <param name="lockdownNote">Who still gets in during a lock-down, e.g. basic-auth users.</param>
    public static NotificationMessage? Describe(string target, IReadOnlyList<NpmClientEntry> before,
        IReadOnlyList<NpmClientEntry> after, IReadOnlyCollection<string> expiredDomains, bool notifyOnChange,
        string lockdownNote)
    {
        if (IsDenyAll(after))
            return new NotificationMessage(NotificationLevel.Critical, "Access list locked down",
                $"{target} was set to 'deny all': the last known IP(s) of "
                + $"{string.Join(", ", expiredDomains)} expired and no static blocks are configured. "
                + $"{lockdownNote} Access is restored automatically once DNS resolves again.");

        if (IsDenyAll(before))
            return new NotificationMessage(NotificationLevel.Info, "Access restored",
                $"{target} allows {after.Count} address(es) again.");

        if (!notifyOnChange)
            return null;

        var beforeSet = before.Select(Key).ToHashSet();
        var afterSet  = after.Select(Key).ToHashSet();

        return new NotificationMessage(NotificationLevel.Info, "Access list updated",
            $"{target}: {afterSet.Except(beforeSet).Count()} rule(s) added, "
            + $"{beforeSet.Except(afterSet).Count()} removed, {after.Count} total.");
    }

    private static bool IsDenyAll(IReadOnlyList<NpmClientEntry> clients) =>
        clients is [{ Directive: "deny", Address: "all" }];

    private static string Key(NpmClientEntry c) => $"{c.Directive} {c.Address}";
}
