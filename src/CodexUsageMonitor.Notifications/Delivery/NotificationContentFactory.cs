using CodexUsageMonitor.Core.Notifications;

namespace CodexUsageMonitor.Notifications.Delivery;

public static class NotificationContentFactory
{
    public static Native.NativeNotificationContent Create(UsageTransition transition, bool playSound)
    {
        ArgumentNullException.ThrowIfNull(transition);
        var remaining = transition.Current?.RemainingPercent;
        var title = transition.Identity.EventType switch
        {
            NotificationEventType.Depleted => "Codex usage depleted",
            NotificationEventType.Reset => "Codex usage reset",
            NotificationEventType.ConnectionLost => "Codex connection lost",
            NotificationEventType.ConnectionRestored => "Codex connection restored",
            NotificationEventType.ResetCreditAvailable => "Reset credit available",
            _ => remaining is null ? "Codex usage changed" : $"{remaining:0}% Codex usage remaining",
        };
        var body = transition.Identity.EventType switch
        {
            NotificationEventType.Depleted => $"{transition.Current?.Label ?? "Selected limit"} has no usage remaining.",
            NotificationEventType.Reset => $"{transition.Current?.Label ?? "Selected limit"} has reset.",
            NotificationEventType.ConnectionLost => "The last valid usage reading is being preserved while Codex reconnects.",
            NotificationEventType.ConnectionRestored => "Live Codex usage data is available again.",
            NotificationEventType.ResetCreditAvailable => "A reset credit is available. Redemption always requires explicit confirmation.",
            _ => $"{transition.Current?.Label ?? "Selected limit"} crossed the {transition.Threshold ?? 0}% threshold.",
        };
        var actions = new List<Native.NativeNotificationAction>
        {
            new("Open widget", "show-widget", transition.Identity.ProfileId.ToString("D")),
            new("Settings", "open-settings", "notifications"),
        };
        if (transition.Identity.EventType is NotificationEventType.ResetCreditAvailable)
        {
            actions.Insert(0, new("Review credit", "review-reset-credit", transition.Identity.ProfileId.ToString("D")));
        }

        return new Native.NativeNotificationContent(title, body, "Codex Usage Monitor", playSound, actions);
    }
}
