using System.Globalization;

namespace CodexUsageMonitor.App.Formatting;

public static class UsageDisplayFormatter
{
    public static string Percentage(decimal value) =>
        $"{Math.Round(Math.Clamp(value, 0m, 100m), 0, MidpointRounding.AwayFromZero).ToString(CultureInfo.CurrentCulture)}%";

    public static string Reset(DateTimeOffset? resetsAtUtc, DateTimeOffset nowUtc)
    {
        if (resetsAtUtc is null)
        {
            return "Reset time unavailable";
        }

        var remaining = resetsAtUtc.Value - nowUtc.ToUniversalTime();
        if (remaining <= TimeSpan.Zero)
        {
            return "Resetting now";
        }

        if (remaining < TimeSpan.FromMinutes(1))
        {
            return "Resets in under a minute";
        }

        if (remaining < TimeSpan.FromHours(1))
        {
            return $"Resets in {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))}m";
        }

        if (remaining < TimeSpan.FromDays(1))
        {
            var hours = (int)remaining.TotalHours;
            var minutes = remaining.Minutes;
            return minutes == 0 ? $"Resets in {hours}h" : $"Resets in {hours}h {minutes}m";
        }

        var days = (int)remaining.TotalDays;
        var trailingHours = remaining.Hours;
        return trailingHours == 0 ? $"Resets in {days}d" : $"Resets in {days}d {trailingHours}h";
    }

    public static string Age(DateTimeOffset? observedAtUtc, DateTimeOffset nowUtc)
    {
        if (observedAtUtc is null) return "No confirmed reading";
        var age = nowUtc.ToUniversalTime() - observedAtUtc.Value;
        if (age < TimeSpan.Zero) return "Clock changed; refresh pending";
        if (age < TimeSpan.FromSeconds(30)) return "Updated just now";
        if (age < TimeSpan.FromMinutes(2)) return $"Updated {(int)age.TotalSeconds}s ago";
        if (age < TimeSpan.FromHours(1)) return $"Updated {(int)age.TotalMinutes}m ago";
        return $"Updated {(int)age.TotalHours}h ago";
    }
}
