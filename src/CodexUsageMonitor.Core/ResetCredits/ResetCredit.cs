namespace CodexUsageMonitor.Core.ResetCredits;

public sealed record ResetCredit
{
    public ResetCredit(
        string id,
        string label,
        IReadOnlyList<string>? affectedLimitIdentities = null,
        DateTimeOffset? expiresAtUtc = null,
        bool isRedeemable = true)
    {
        Id = Normalize(id, 192, nameof(id));
        Label = Normalize(label, 120, nameof(label));
        AffectedLimitIdentities = affectedLimitIdentities?
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(16)
            .ToArray() ?? [];
        ExpiresAtUtc = expiresAtUtc?.ToUniversalTime();
        IsRedeemable = isRedeemable;
    }

    public string Id { get; }

    public string Label { get; }

    public IReadOnlyList<string> AffectedLimitIdentities { get; }

    public DateTimeOffset? ExpiresAtUtc { get; }

    public bool IsRedeemable { get; }

    public bool IsExpired(DateTimeOffset nowUtc) => ExpiresAtUtc is { } expiry && expiry <= nowUtc.ToUniversalTime();

    private static string Normalize(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : throw new ArgumentOutOfRangeException(parameterName);
    }
}
