using CodexUsageMonitor.Core.Accounts;
using CodexUsageMonitor.Core.Usage;
using CodexUsageMonitor.Core.ResetCredits;

namespace CodexUsageMonitor.Codex.Mapping;

public sealed record MappedAccountSnapshot(
    AccountIdentity Account,
    IReadOnlyList<UsageLimit> Limits,
    IReadOnlyList<ResetCredit> ResetCredits,
    string? Workspace,
    long Sequence);
