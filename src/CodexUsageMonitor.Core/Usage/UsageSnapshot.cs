using CodexUsageMonitor.Core.Accounts;
using CodexUsageMonitor.Core.ResetCredits;

namespace CodexUsageMonitor.Core.Usage;

public sealed record UsageSnapshot
{
    public UsageSnapshot(
        Guid profileId,
        AccountIdentity account,
        DateTimeOffset observedAtUtc,
        IReadOnlyList<UsageLimit> limits,
        IReadOnlyList<ResetCredit>? availableResetCredits = null,
        string? workspace = null,
        long sequence = 0)
    {
        ProfileId = profileId == Guid.Empty ? throw new ArgumentException("Profile ID cannot be empty.", nameof(profileId)) : profileId;
        Account = account ?? throw new ArgumentNullException(nameof(account));
        ObservedAtUtc = observedAtUtc.ToUniversalTime();
        Limits = limits?.Where(static limit => limit is not null).ToArray() ?? throw new ArgumentNullException(nameof(limits));
        AvailableResetCredits = availableResetCredits?.Where(static credit => credit is not null).ToArray() ?? [];
        Workspace = string.IsNullOrWhiteSpace(workspace) ? null : workspace.Trim();
        Sequence = Math.Max(0, sequence);
    }

    public Guid ProfileId { get; }

    public AccountIdentity Account { get; }

    public DateTimeOffset ObservedAtUtc { get; }

    public IReadOnlyList<UsageLimit> Limits { get; }

    public IReadOnlyList<ResetCredit> AvailableResetCredits { get; }

    public int ResetCredits => AvailableResetCredits.Count;

    public string? Workspace { get; }

    public long Sequence { get; }

    public UsageLimit? Find(string identity) =>
        Limits.FirstOrDefault(limit => StringComparer.Ordinal.Equals(limit.Identity, identity));
}
