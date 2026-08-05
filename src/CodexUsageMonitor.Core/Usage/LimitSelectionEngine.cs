namespace CodexUsageMonitor.Core.Usage;

public enum LimitSelectionMode
{
    AutoLowest = 0,
    FiveHour = 1,
    Weekly = 2,
    ModelSpecific = 3,
    Credits = 4,
    Explicit = 5,
}

public sealed record LimitSelectionRequest(
    LimitSelectionMode Mode,
    string? ExplicitIdentity = null,
    string? PreferredModel = null,
    bool DualMeter = false);

public sealed record LimitSelectionResult(
    UsageLimit? Primary,
    UsageLimit? Secondary,
    string Code)
{
    public bool HasData => Primary is not null;
}

public static class LimitSelectionEngine
{
    public static LimitSelectionResult Select(
        IReadOnlyList<UsageLimit> limits,
        LimitSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(request);
        var usable = limits.Where(static limit => limit.IsAuthoritative).ToArray();
        if (usable.Length == 0)
        {
            return new LimitSelectionResult(null, null, "selection.no_limits");
        }

        var primary = request.Mode switch
        {
            LimitSelectionMode.AutoLowest => AutoLowest(usable),
            LimitSelectionMode.FiveHour => usable.FirstOrDefault(static x => x.Kind is LimitKind.FiveHour),
            LimitSelectionMode.Weekly => usable.FirstOrDefault(static x => x.Kind is LimitKind.Weekly),
            LimitSelectionMode.ModelSpecific => usable.FirstOrDefault(x =>
                x.Kind is LimitKind.ModelSpecific &&
                (string.IsNullOrWhiteSpace(request.PreferredModel) ||
                 StringComparer.OrdinalIgnoreCase.Equals(x.Model, request.PreferredModel))),
            LimitSelectionMode.Credits => usable.FirstOrDefault(static x => x.Kind is LimitKind.Credits),
            LimitSelectionMode.Explicit => usable.FirstOrDefault(x =>
                StringComparer.Ordinal.Equals(x.Identity, request.ExplicitIdentity)),
            _ => null,
        };

        if (primary is null)
        {
            primary = AutoLowest(usable);
        }

        UsageLimit? secondary = null;
        if (request.DualMeter && primary is not null)
        {
            secondary = usable
                .Where(limit => !StringComparer.Ordinal.Equals(limit.Identity, primary.Identity))
                .OrderBy(static limit => limit.RemainingPercent)
                .ThenBy(static limit => LimitPriority(limit.Kind))
                .FirstOrDefault();
        }

        return new LimitSelectionResult(
            primary,
            secondary,
            request.Mode is LimitSelectionMode.AutoLowest ? "selection.auto" : "selection.selected");
    }

    private static UsageLimit AutoLowest(IEnumerable<UsageLimit> limits) =>
        limits
            .OrderBy(static limit => limit.RemainingPercent)
            .ThenBy(static limit => LimitPriority(limit.Kind))
            .ThenBy(static limit => limit.ResetsAtUtc ?? DateTimeOffset.MaxValue)
            .ThenBy(static limit => limit.Identity, StringComparer.Ordinal)
            .First();

    private static int LimitPriority(LimitKind kind) => kind switch
    {
        LimitKind.FiveHour => 0,
        LimitKind.Weekly => 1,
        LimitKind.ModelSpecific => 2,
        LimitKind.Credits => 3,
        _ => 4,
    };
}
