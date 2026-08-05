using System.Globalization;
using System.Text.Json;
using CodexUsageMonitor.Core.Accounts;
using CodexUsageMonitor.Core.Diagnostics;
using CodexUsageMonitor.Core.Usage;
using CodexUsageMonitor.Core.ResetCredits;

namespace CodexUsageMonitor.Codex.Mapping;

public sealed class CodexSnapshotMapper
{
    private readonly IProtocolAnomalySink _anomalySink;

    public CodexSnapshotMapper(IProtocolAnomalySink anomalySink)
    {
        _anomalySink = anomalySink ?? throw new ArgumentNullException(nameof(anomalySink));
    }

    public MappedAccountSnapshot Map(JsonElement accountRead, JsonElement rateLimitsRead)
    {
        var accountObject = UnwrapObject(accountRead, "account");
        var stableId = FirstString(accountObject, "id", "accountId", "userId", "email")
            ?? throw new InvalidDataException("Codex account response did not contain a stable identity.");
        var account = new AccountIdentity(
            stableId,
            FirstString(accountObject, "email"),
            FirstString(accountObject, "name", "displayName"),
            FirstString(accountObject, "organization", "organizationName"));

        var container = UnwrapObject(rateLimitsRead, "rateLimits");
        var limits = new Dictionary<string, UsageLimit>(StringComparer.Ordinal);
        foreach (var candidate in EnumerateLimitCandidates(container))
        {
            try
            {
                var limit = MapLimit(candidate.Name, candidate.Value);
                if (limit is not null)
                {
                    limits[limit.Identity] = limit;
                }
            }
            catch (InvalidDataException)
            {
                _anomalySink.Report("codex.limit_invalid", new Dictionary<string, string>
                {
                    ["property"] = candidate.Name,
                });
            }
        }

        if (limits.Count == 0)
        {
            _anomalySink.Report("codex.no_mappable_limits");
        }

        var resetCredits = ReadResetCredits(container);
        var workspace = FirstString(rateLimitsRead, "workspace", "workspaceName");
        var sequence = FirstInt64(container, "sequence", "version") ?? 0;
        return new MappedAccountSnapshot(account, limits.Values.ToArray(), resetCredits, workspace, sequence);
    }

    private static UsageLimit? MapLimit(string propertyName, JsonElement value)
    {
        if (value.ValueKind is not JsonValueKind.Object)
        {
            return null;
        }

        var used = FirstDecimal(value, "usedPercent", "used_percentage", "percentUsed");
        var remaining = FirstDecimal(value, "remainingPercent", "remaining_percentage", "percentRemaining");
        if (used is null && remaining is null)
        {
            var total = FirstDecimal(value, "totalCredits", "total", "limit");
            var available = FirstDecimal(value, "remainingCredits", "remaining", "balance");
            if (total is > 0 && available is not null)
            {
                remaining = Math.Clamp(available.Value / total.Value * 100m, 0m, 100m);
            }
            else
            {
                return null;
            }
        }

        var windowSeconds = FirstInt64(value, "windowSeconds", "windowDurationSeconds") ??
            MultiplySafely(FirstInt64(value, "windowDurationMins", "windowMinutes"), 60);
        var model = FirstString(value, "model", "modelName");
        var serverId = FirstString(value, "id", "limitId", "key");
        var kind = InferKind(propertyName, model, windowSeconds);
        var identity = LimitIdentityFactory.Create(new LimitIdentityInput(
            serverId,
            kind,
            model,
            windowSeconds,
            propertyName));
        var label = LimitLabelResolver.Resolve(
            kind,
            model,
            windowSeconds,
            FirstString(value, "label", "displayName"));
        return new UsageLimit(
            identity,
            kind,
            label,
            UsageMath.NormalizeUsedPercent(used, remaining),
            ReadDate(value, "resetsAt", "resetAt", "resets_at"),
            model,
            FirstDecimal(value, "remainingCredits", "remaining", "balance"),
            FirstDecimal(value, "totalCredits", "total", "limit"));
    }

    private static IEnumerable<(string Name, JsonElement Value)> EnumerateLimitCandidates(JsonElement container)
    {
        if (container.ValueKind is not JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var property in container.EnumerateObject())
        {
            if (property.NameEquals("resetCredits") || property.NameEquals("resetCredit") ||
                property.NameEquals("planType") || property.NameEquals("sequence"))
            {
                continue;
            }

            if (property.Value.ValueKind is JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in property.Value.EnumerateArray())
                {
                    yield return ($"{property.Name}:{index++}", item);
                }
            }
            else if (property.Value.ValueKind is JsonValueKind.Object)
            {
                if (LooksLikeLimit(property.Value))
                {
                    yield return (property.Name, property.Value);
                }
                else
                {
                    foreach (var child in property.Value.EnumerateObject())
                    {
                        if (child.Value.ValueKind is JsonValueKind.Object && LooksLikeLimit(child.Value))
                        {
                            yield return ($"{property.Name}:{child.Name}", child.Value);
                        }
                    }
                }
            }
        }
    }

    private static bool LooksLikeLimit(JsonElement value) =>
        value.TryGetProperty("usedPercent", out _) ||
        value.TryGetProperty("remainingPercent", out _) ||
        value.TryGetProperty("windowDurationMins", out _) ||
        value.TryGetProperty("resetsAt", out _) ||
        value.TryGetProperty("balance", out _);

    private static LimitKind InferKind(string propertyName, string? model, long? windowSeconds)
    {
        if (propertyName.Contains("credit", StringComparison.OrdinalIgnoreCase))
        {
            return LimitKind.Credits;
        }

        if (!string.IsNullOrWhiteSpace(model) || propertyName.Contains("model", StringComparison.OrdinalIgnoreCase))
        {
            return LimitKind.ModelSpecific;
        }

        if (windowSeconds is > 0 and <= 6 * 60 * 60)
        {
            return LimitKind.FiveHour;
        }

        if (windowSeconds is >= 5 * 24 * 60 * 60)
        {
            return LimitKind.Weekly;
        }

        if (propertyName.Contains("primary", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Contains("five", StringComparison.OrdinalIgnoreCase))
        {
            return LimitKind.FiveHour;
        }

        if (propertyName.Contains("secondary", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Contains("week", StringComparison.OrdinalIgnoreCase))
        {
            return LimitKind.Weekly;
        }

        return LimitKind.Dynamic;
    }

    private static IReadOnlyList<ResetCredit> ReadResetCredits(JsonElement container)
    {
        foreach (var name in new[] { "resetCredits", "rateLimitResetCredits" })
        {
            if (!container.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind is JsonValueKind.Array)
            {
                return ParseResetCreditArray(value);
            }

            if (value.ValueKind is JsonValueKind.Object)
            {
                foreach (var childName in new[] { "credits", "items", "availableCredits" })
                {
                    if (value.TryGetProperty(childName, out var items) && items.ValueKind is JsonValueKind.Array)
                    {
                        return ParseResetCreditArray(items);
                    }
                }

                if (TryMapResetCredit(value, 0, out var single))
                {
                    return [single];
                }

                if (value.TryGetProperty("available", out var available) && available.TryGetInt32(out var objectCount))
                {
                    return UnknownResetCredits(objectCount);
                }
            }

            if (value.ValueKind is JsonValueKind.Number && value.TryGetInt32(out var count))
            {
                return UnknownResetCredits(count);
            }
        }

        return [];
    }

    private static IReadOnlyList<ResetCredit> ParseResetCreditArray(JsonElement array)
    {
        var credits = new List<ResetCredit>();
        var index = 0;
        foreach (var value in array.EnumerateArray())
        {
            if (TryMapResetCredit(value, index++, out var credit))
            {
                credits.Add(credit);
            }
        }

        return credits;
    }

    private static bool TryMapResetCredit(JsonElement value, int index, out ResetCredit credit)
    {
        if (value.ValueKind is JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
        {
            credit = new ResetCredit(value.GetString()!, "Usage reset");
            return true;
        }

        if (value.ValueKind is not JsonValueKind.Object)
        {
            credit = null!;
            return false;
        }

        var id = FirstString(value, "id", "resetCreditId", "creditId", "key");
        var redeemable = !string.IsNullOrWhiteSpace(id);
        id ??= $"unidentified-{index}";
        var label = FirstString(value, "label", "displayName", "name") ?? "Usage reset";
        var affected = ReadStringArray(value, "affectedLimits", "limits", "resets");
        var expiresAt = ReadDate(value, "expiresAt", "expires_at", "expiration");
        credit = new ResetCredit(id, label, affected, expiresAt, redeemable);
        return true;
    }

    private static IReadOnlyList<ResetCredit> UnknownResetCredits(int count) =>
        Enumerable.Range(0, Math.Clamp(count, 0, 100))
            .Select(static index => new ResetCredit($"unidentified-{index}", "Usage reset", isRedeemable: false))
            .ToArray();

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value) || value.ValueKind is not JsonValueKind.Array)
            {
                continue;
            }

            return value.EnumerateArray()
                .Select(static item => item.ValueKind is JsonValueKind.String ? item.GetString() : null)
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Select(static item => item!.Trim())
                .Take(16)
                .ToArray();
        }

        return [];
    }

    private static JsonElement UnwrapObject(JsonElement element, string property)
    {
        if (element.ValueKind is not JsonValueKind.Object)
        {
            throw new InvalidDataException("Codex response must be a JSON object.");
        }

        return element.TryGetProperty(property, out var nested) && nested.ValueKind is JsonValueKind.Object
            ? nested
            : element;
    }

    private static string? FirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind is JsonValueKind.Object &&
                element.TryGetProperty(name, out var value) &&
                value.ValueKind is JsonValueKind.String)
            {
                var text = value.GetString()?.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static decimal? FirstDecimal(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind is JsonValueKind.Number && value.TryGetDecimal(out var number))
            {
                return number;
            }

            if (value.ValueKind is JsonValueKind.String &&
                decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out number))
            {
                return number;
            }
        }

        return null;
    }

    private static long? FirstInt64(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number))
            {
                return number;
            }
        }

        return null;
    }

    private static DateTimeOffset? ReadDate(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind is JsonValueKind.Number && value.TryGetInt64(out var epoch))
            {
                try
                {
                    return epoch > 10_000_000_000
                        ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                        : DateTimeOffset.FromUnixTimeSeconds(epoch);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return null;
                }
            }

            if (value.ValueKind is JsonValueKind.String &&
                DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed.ToUniversalTime();
            }
        }

        return null;
    }

    private static long? MultiplySafely(long? value, long multiplier)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return checked(value.Value * multiplier);
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}
