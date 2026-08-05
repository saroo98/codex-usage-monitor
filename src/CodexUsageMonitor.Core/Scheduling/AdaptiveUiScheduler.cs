using CodexUsageMonitor.Core.Monitoring;

namespace CodexUsageMonitor.Core.Scheduling;

public static class AdaptiveUiScheduler
{
    public static readonly TimeSpan DelayedAfter = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MinimumDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromHours(24);

    public static TimeSpan? NextDelay(
        MonitorState state,
        bool isWidgetVisible,
        bool isHovering,
        DateTimeOffset nowUtc,
        DateTimeOffset? resetAtUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        var candidates = new List<DateTimeOffset>(4);
        if (state.LastSuccessAtUtc is { } lastSuccess)
        {
            AddFuture(candidates, lastSuccess + DelayedAfter, nowUtc);
            AddFuture(candidates, lastSuccess + StaleAfter, nowUtc);
        }

        if (isWidgetVisible || isHovering)
        {
            if (resetAtUtc is { } reset)
            {
                AddFuture(candidates, reset, nowUtc);
                var remaining = reset - nowUtc;
                if (remaining > TimeSpan.Zero)
                {
                    candidates.Add(remaining <= TimeSpan.FromHours(24)
                        ? NextMinuteBoundary(nowUtc)
                        : NextHourBoundary(nowUtc));
                }
            }

            if (isHovering)
            {
                candidates.Add(NextMinuteBoundary(nowUtc));
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        var delay = candidates.Min() - nowUtc;
        if (delay < MinimumDelay)
        {
            return MinimumDelay;
        }

        return delay > MaximumDelay ? MaximumDelay : delay;
    }

    public static TimeSpan NextTick(
        MonitorState state,
        bool isWidgetVisible,
        bool isHovering,
        DateTimeOffset nowUtc,
        DateTimeOffset? resetAtUtc) =>
        NextDelay(state, isWidgetVisible, isHovering, nowUtc, resetAtUtc) ?? MaximumDelay;

    public static TimeSpan NextTick(MonitorState state, bool isWidgetVisible, bool isHovering) =>
        NextTick(state, isWidgetVisible, isHovering, DateTimeOffset.UtcNow, null);

    private static void AddFuture(List<DateTimeOffset> candidates, DateTimeOffset candidate, DateTimeOffset nowUtc)
    {
        if (candidate > nowUtc)
        {
            candidates.Add(candidate);
        }
    }

    private static DateTimeOffset NextMinuteBoundary(DateTimeOffset nowUtc)
    {
        var truncated = new DateTimeOffset(
            nowUtc.Year,
            nowUtc.Month,
            nowUtc.Day,
            nowUtc.Hour,
            nowUtc.Minute,
            0,
            nowUtc.Offset);
        return truncated.AddMinutes(1);
    }

    private static DateTimeOffset NextHourBoundary(DateTimeOffset nowUtc)
    {
        var truncated = new DateTimeOffset(
            nowUtc.Year,
            nowUtc.Month,
            nowUtc.Day,
            nowUtc.Hour,
            0,
            0,
            nowUtc.Offset);
        return truncated.AddHours(1);
    }
}
