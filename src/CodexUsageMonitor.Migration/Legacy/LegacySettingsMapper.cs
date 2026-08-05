using System.Text.Json;
using CodexUsageMonitor.Core.Settings;
using CodexUsageMonitor.Core.Usage;

namespace CodexUsageMonitor.Migration.Legacy;

public sealed record LegacyMappingResult(AppSettings Settings, IReadOnlyList<string> Warnings, bool BaselineAvailable);

public sealed class LegacySettingsMapper
{
    public LegacyMappingResult Map(AppSettings current, JsonElement config, JsonElement? uiState, JsonElement? state)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (config.ValueKind is not JsonValueKind.Object)
        {
            throw new InvalidDataException("Legacy configuration is invalid.");
        }

        var warnings = new List<string>();
        var schema = GetInt(config, "schema_version") ?? 0;
        if (schema is < 4 or > 5)
        {
            warnings.Add("migration.legacy_schema_unverified");
        }

        var ui = GetObject(config, "ui");
        var notification = GetObject(config, "notification");
        var topmost = GetBool(uiState, "always_on_top") ?? GetBool(ui, "always_on_top") ?? current.Widget.Topmost;
        var showReset = GetBool(ui, "show_reset_countdown") ?? current.Widget.ResetTimeDisplay is not ResetTimeDisplayMode.Hidden;
        var sound = GetBool(notification, "sound") ?? current.Notifications.PlaySound;
        var notificationEnabled =
            (GetBool(notification, "toast") ?? false) ||
            (GetBool(notification, "tray_balloon") ?? false) ||
            (GetBool(notification, "popup") ?? false);
        var preferred = GetString(uiState, "preferred_meter") ?? GetString(ui, "preferred_meter") ?? "auto";
        var explicitIdentity = string.Equals(preferred, "auto", StringComparison.OrdinalIgnoreCase) ? null : preferred;
        var mode = explicitIdentity is null ? LimitSelectionMode.AutoLowest : LimitSelectionMode.Explicit;
        var placement = TryMapPlacement(uiState, current.Widget.Placement);
        var codexCommand = GetString(config, "codex_command");
        if (!string.IsNullOrWhiteSpace(codexCommand) && !string.Equals(codexCommand, "codex", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("migration.custom_codex_command_requires_review");
        }

        var mapped = current with
        {
            Widget = current.Widget with
            {
                Topmost = topmost,
                ResetTimeDisplay = showReset ? ResetTimeDisplayMode.Countdown : ResetTimeDisplayMode.Hidden,
                Placement = placement,
            },
            Limits = current.Limits with
            {
                SelectionMode = mode,
                ExplicitLimitIdentity = explicitIdentity,
            },
            Notifications = current.Notifications with
            {
                Enabled = notificationEnabled,
                PlaySound = sound,
            },
            General = current.General with { ShowOnboardingOnNextLaunch = true },
        };
        var baselineAvailable = state is { ValueKind: JsonValueKind.Object } &&
            state.Value.TryGetProperty("last_snapshot", out var snapshot) && snapshot.ValueKind is JsonValueKind.Object;
        if (baselineAvailable)
        {
            warnings.Add("migration.baseline_preserved_in_backup_not_cross_account_imported");
        }

        return new LegacyMappingResult(SettingsValidation.Normalize(mapped).Settings, warnings.AsReadOnly(), baselineAvailable);
    }

    private static WidgetPlacement? TryMapPlacement(JsonElement? state, WidgetPlacement? fallback)
    {
        var left = GetDouble(state, "left");
        var top = GetDouble(state, "top");
        if (left is null || top is null || !double.IsFinite(left.Value) || !double.IsFinite(top.Value))
        {
            return fallback;
        }

        return new WidgetPlacement(
            fallback?.MonitorDeviceName ?? "",
            left.Value,
            top.Value,
            fallback?.WidthDip ?? 208,
            fallback?.HeightDip ?? 60,
            fallback?.DpiScaleX ?? 1,
            fallback?.DpiScaleY ?? 1,
            DateTimeOffset.UtcNow);
    }

    private static JsonElement? GetObject(JsonElement? root, string name) =>
        root is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var child) && child.ValueKind is JsonValueKind.Object
            ? child
            : null;

    private static string? GetString(JsonElement? root, string name) =>
        root is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var child) && child.ValueKind is JsonValueKind.String
            ? child.GetString()?.Trim()
            : null;

    private static bool? GetBool(JsonElement? root, string name) =>
        root is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var child) && child.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? child.GetBoolean()
            : null;

    private static int? GetInt(JsonElement? root, string name) =>
        root is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var child) && child.TryGetInt32(out var number)
            ? number
            : null;

    private static double? GetDouble(JsonElement? root, string name) =>
        root is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var child) && child.TryGetDouble(out var number)
            ? number
            : null;
}
