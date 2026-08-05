namespace CodexUsageMonitor.Core.Usage;

public sealed record UsageLimit
{
    public UsageLimit(
        string identity,
        LimitKind kind,
        string label,
        decimal usedPercent,
        DateTimeOffset? resetsAtUtc,
        string? model = null,
        decimal? remainingCredits = null,
        decimal? totalCredits = null,
        bool isAuthoritative = true)
    {
        Identity = Normalize(identity, 192, nameof(identity));
        Kind = kind;
        Label = Normalize(label, 96, nameof(label));
        UsedPercent = UsageMath.ClampPercentage(usedPercent);
        RemainingPercent = UsageMath.ClampPercentage(100m - UsedPercent);
        ResetsAtUtc = resetsAtUtc?.ToUniversalTime();
        Model = NormalizeOptional(model, 96);
        RemainingCredits = remainingCredits is < 0 ? 0 : remainingCredits;
        TotalCredits = totalCredits is <= 0 ? null : totalCredits;
        IsAuthoritative = isAuthoritative;
    }

    public string Identity { get; }

    public LimitKind Kind { get; }

    public string Label { get; }

    public decimal UsedPercent { get; }

    public decimal RemainingPercent { get; }

    public DateTimeOffset? ResetsAtUtc { get; }

    public string? Model { get; }

    public decimal? RemainingCredits { get; }

    public decimal? TotalCredits { get; }

    public bool IsAuthoritative { get; }

    public TimeSpan? TimeUntilReset(DateTimeOffset nowUtc) =>
        ResetsAtUtc is null ? null : ResetsAtUtc.Value - nowUtc.ToUniversalTime();

    private static string Normalize(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : throw new ArgumentOutOfRangeException(parameterName);
    }

    private static string? NormalizeOptional(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : throw new ArgumentOutOfRangeException(nameof(value));
    }
}
