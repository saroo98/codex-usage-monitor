using CodexUsageMonitor.Core.Usage;

namespace CodexUsageMonitor.Persistence.History;

public sealed record HistoryPoint(
    DateTimeOffset ObservedAtUtc,
    decimal RemainingPercent,
    decimal UsedPercent,
    DateTimeOffset? ResetsAtUtc);

public sealed record DailyUsagePoint(
    DateOnly DayUtc,
    decimal MinimumRemaining,
    decimal MaximumRemaining,
    decimal FirstRemaining,
    decimal LastRemaining,
    int SampleCount);

public sealed record DepletionProjection(
    bool IsAvailable,
    DateTimeOffset? EstimatedDepletionAtUtc,
    decimal PercentagePointsPerHour,
    string Code);
