namespace CodexUsageMonitor.Core.Usage;

public static class UsageMath
{
    public static decimal ClampPercentage(decimal value) => Math.Clamp(value, 0m, 100m);

    public static decimal NormalizeUsedPercent(decimal? usedPercent, decimal? remainingPercent)
    {
        if (usedPercent is not null)
        {
            return ClampPercentage(usedPercent.Value);
        }

        if (remainingPercent is not null)
        {
            return ClampPercentage(100m - remainingPercent.Value);
        }

        throw new InvalidDataException("A usage limit must contain used or remaining percentage data.");
    }

    public static int RoundedRemaining(decimal remainingPercent) =>
        decimal.ToInt32(decimal.Round(ClampPercentage(remainingPercent), 0, MidpointRounding.AwayFromZero));
}
