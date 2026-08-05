using System.Text.Json;

namespace CodexUsageMonitor.Codex.Contracts;

public sealed record AppServerInitialization(
    string? CodexHome,
    string? Platform,
    string? UserAgent,
    JsonElement Raw);

public sealed record AccountReadResult(JsonElement Raw);

public sealed record RateLimitsReadResult(JsonElement Raw);

public sealed record UsageReadResult(bool Supported, JsonElement Raw);

public enum ResetCreditConsumeOutcome
{
    Reset,
    AlreadyRedeemed,
    NothingToReset,
    NoCredit,
    Unsupported,
}

public sealed record ResetCreditConsumeResult(
    ResetCreditConsumeOutcome Outcome,
    string Code,
    JsonElement Raw)
{
    public bool Succeeded => Outcome is ResetCreditConsumeOutcome.Reset or ResetCreditConsumeOutcome.AlreadyRedeemed;

    public bool AlreadyRedeemed => Outcome is ResetCreditConsumeOutcome.AlreadyRedeemed;

    public bool ShouldRefreshLimits => Succeeded;

    public static ResetCreditConsumeResult FromRaw(JsonElement raw)
    {
        var outcome = ReadOutcome(raw);
        return new ResetCreditConsumeResult(outcome, ToCode(outcome), raw.Clone());
    }

    public static ResetCreditConsumeResult Rejected(string code) =>
        new(ResetCreditConsumeOutcome.Unsupported, code, default);

    private static ResetCreditConsumeOutcome ReadOutcome(JsonElement raw)
    {
        if (raw.ValueKind is not JsonValueKind.Object ||
            !raw.TryGetProperty("outcome", out var value) ||
            value.ValueKind is not JsonValueKind.String)
        {
            return ResetCreditConsumeOutcome.Unsupported;
        }

        return value.GetString()?.Trim().ToLowerInvariant() switch
        {
            "reset" => ResetCreditConsumeOutcome.Reset,
            "alreadyredeemed" or "already_redeemed" or "already-redeemed" => ResetCreditConsumeOutcome.AlreadyRedeemed,
            "nothingtoreset" or "nothing_to_reset" or "nothing-to-reset" => ResetCreditConsumeOutcome.NothingToReset,
            "nocredit" or "no_credit" or "no-credit" => ResetCreditConsumeOutcome.NoCredit,
            _ => ResetCreditConsumeOutcome.Unsupported,
        };
    }

    private static string ToCode(ResetCreditConsumeOutcome outcome) => outcome switch
    {
        ResetCreditConsumeOutcome.Reset => "reset_credit.reset",
        ResetCreditConsumeOutcome.AlreadyRedeemed => "reset_credit.already_redeemed",
        ResetCreditConsumeOutcome.NothingToReset => "reset_credit.nothing_to_reset",
        ResetCreditConsumeOutcome.NoCredit => "reset_credit.no_credit",
        _ => "reset_credit.unsupported_outcome",
    };
}
