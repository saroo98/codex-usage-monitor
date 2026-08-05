namespace CodexUsageMonitor.Core.Notifications;

public sealed record QuietHoursSchedule(bool Enabled, TimeOnly Start, TimeOnly End)
{
    public bool IsQuiet(DateTimeOffset utcNow, TimeZoneInfo timeZone)
    {
        if (!Enabled)
        {
            return false;
        }

        var local = TimeZoneInfo.ConvertTime(utcNow, timeZone);
        var current = TimeOnly.FromDateTime(local.DateTime);
        return Start == End ||
            (Start < End ? current >= Start && current < End : current >= Start || current < End);
    }

    public DateTimeOffset NextEnd(DateTimeOffset utcNow, TimeZoneInfo timeZone)
    {
        if (!Enabled)
        {
            return utcNow;
        }

        var local = TimeZoneInfo.ConvertTime(utcNow, timeZone);
        var localDate = DateOnly.FromDateTime(local.DateTime);
        var endDate = Start < End
            ? localDate
            : TimeOnly.FromDateTime(local.DateTime) < End
                ? localDate
                : localDate.AddDays(1);
        var unspecified = DateTime.SpecifyKind(endDate.ToDateTime(End), DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1);
        }

        var offset = timeZone.GetUtcOffset(unspecified);
        return new DateTimeOffset(unspecified, offset).ToUniversalTime();
    }
}
