using System.IO;

namespace CodexUsageMonitor.Windows.Runtime;

public sealed record AppLaunchRequest(
    bool IsValid,
    bool Background,
    IReadOnlyList<ActivationCommand> Commands,
    string? SafeErrorCode,
    int ExitCode)
{
    public bool ApplyLaunchMinimizedPreference { get; init; }

    public bool HasPortableUpdateCommand => Commands.Any(static command =>
        command.Name is ActivationCommandNames.UpdateHealth or ActivationCommandNames.UpdateRolledBack);

    public ActivationMessage ToActivationMessage() =>
        new(ActivationMessage.CurrentVersion, Commands);
}

public static class AppCommandLine
{
    private static readonly IReadOnlyDictionary<string, string> SettingsSections =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["General"] = "General",
            ["Widget"] = "Widget",
            ["Limits"] = "Limits",
            ["Notifications"] = "Notifications",
            ["Email"] = "Email",
            ["Accounts"] = "Accounts",
            ["History"] = "History",
            ["Updates"] = "Updates",
            ["Diagnostics"] = "Diagnostics",
        };

    public const int MaximumArguments = 32;
    public const int MaximumArgumentCharacters = 2048;
    public const int MaximumTotalCharacters = 8192;

    public static AppLaunchRequest Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count > MaximumArguments)
        {
            return Invalid("command_line.too_many_arguments");
        }

        var totalCharacters = 0;
        var background = false;
        var commands = new List<ActivationCommand>();
        Guid? updateTransactionId = null;
        string? healthMarkerPath = null;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index] ?? string.Empty;
            if (!TryAccumulate(argument, ref totalCharacters))
            {
                return Invalid(argument.Length > MaximumArgumentCharacters
                    ? "command_line.argument_too_long"
                    : "command_line.too_long");
            }

            if (string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase))
            {
                background = true;
            }
            else if (string.Equals(argument, "--show-widget", StringComparison.OrdinalIgnoreCase))
            {
                AddUnique(commands, new ActivationCommand(ActivationCommandNames.ShowWidget));
            }
            else if (string.Equals(argument, "--settings", StringComparison.OrdinalIgnoreCase))
            {
                AddUnique(commands, new ActivationCommand(ActivationCommandNames.OpenSettings));
            }
            else if (argument.StartsWith("--settings=", StringComparison.OrdinalIgnoreCase))
            {
                var section = argument[(argument.IndexOf('=') + 1)..].Trim();
                if (!SettingsSections.TryGetValue(section, out var normalizedSection))
                {
                    return Invalid("command_line.settings_section_invalid");
                }

                AddUnique(commands, new ActivationCommand(ActivationCommandNames.OpenSettings, normalizedSection));
            }
            else if (string.Equals(argument, "--diagnostics", StringComparison.OrdinalIgnoreCase))
            {
                AddUnique(commands, new ActivationCommand(ActivationCommandNames.OpenDiagnostics));
            }
            else if (string.Equals(argument, "--refresh", StringComparison.OrdinalIgnoreCase))
            {
                AddUnique(commands, new ActivationCommand(ActivationCommandNames.Refresh));
            }
            else if (string.Equals(argument, "--disable-click-through", StringComparison.OrdinalIgnoreCase))
            {
                AddUnique(commands, new ActivationCommand(ActivationCommandNames.DisableClickThrough));
            }
            else if (TryReadValue(arguments, ref index, argument, "--review-reset-credit", ref totalCharacters, out var reviewValue, out var reviewError))
            {
                if (reviewError is not null || !Guid.TryParse(reviewValue, out var profileId) || profileId == Guid.Empty)
                {
                    return Invalid(reviewError ?? "command_line.reset_credit_profile_invalid");
                }

                AddUnique(commands, new ActivationCommand(ActivationCommandNames.ReviewResetCredit, profileId.ToString("D")));
            }
            else if (TryReadValue(arguments, ref index, argument, "--after-update", ref totalCharacters, out var transactionValue, out var transactionError))
            {
                if (transactionError is not null || !Guid.TryParse(transactionValue, out var updateTransaction) || updateTransaction == Guid.Empty)
                {
                    return Invalid(transactionError ?? "command_line.update_transaction_invalid");
                }

                updateTransactionId = updateTransaction;
            }
            else if (TryReadValue(arguments, ref index, argument, "--health-marker", ref totalCharacters, out var markerValue, out var markerError))
            {
                if (markerError is not null || string.IsNullOrWhiteSpace(markerValue) || markerValue.Length > 2048 ||
                    markerValue.IndexOfAny(['\0', '\r', '\n']) >= 0 || !Path.IsPathFullyQualified(markerValue))
                {
                    return Invalid(markerError ?? "command_line.health_marker_invalid");
                }

                healthMarkerPath = markerValue;
            }
            else if (TryReadValue(arguments, ref index, argument, "--update-rolled-back", ref totalCharacters, out var rollbackValue, out var rollbackError))
            {
                if (rollbackError is not null || !Guid.TryParse(rollbackValue, out var rollbackTransaction) || rollbackTransaction == Guid.Empty)
                {
                    return Invalid(rollbackError ?? "command_line.rollback_transaction_invalid");
                }

                AddUnique(commands, new ActivationCommand(ActivationCommandNames.UpdateRolledBack, rollbackTransaction.ToString("D")));
            }
            else
            {
                return Invalid("command_line.unknown_option");
            }
        }

        if (updateTransactionId.HasValue != (healthMarkerPath is not null))
        {
            return Invalid("command_line.update_health_incomplete");
        }

        if (updateTransactionId is { } transactionId && healthMarkerPath is not null)
        {
            var request = new StartupHealthRequest(transactionId, healthMarkerPath);
            AddUnique(commands, new ActivationCommand(ActivationCommandNames.UpdateHealth, request.Encode()));
        }

        if (commands.Count == 0)
        {
            commands.Add(new ActivationCommand(background
                ? ActivationCommandNames.HideWidget
                : ActivationCommandNames.ShowWidget));
        }

        return new AppLaunchRequest(true, background, commands.AsReadOnly(), null, 0)
        {
            ApplyLaunchMinimizedPreference = arguments.Count == 0,
        };
    }

    private static bool TryReadValue(
        IReadOnlyList<string> arguments,
        ref int index,
        string argument,
        string optionName,
        ref int totalCharacters,
        out string? value,
        out string? safeErrorCode)
    {
        value = null;
        safeErrorCode = null;
        if (argument.StartsWith(optionName + "=", StringComparison.OrdinalIgnoreCase))
        {
            value = argument[(optionName.Length + 1)..].Trim();
            if (string.IsNullOrEmpty(value)) safeErrorCode = "command_line.option_value_missing";
            return true;
        }

        if (!string.Equals(argument, optionName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (++index >= arguments.Count)
        {
            safeErrorCode = "command_line.option_value_missing";
            return true;
        }

        value = arguments[index] ?? string.Empty;
        if (!TryAccumulate(value, ref totalCharacters))
        {
            safeErrorCode = value.Length > MaximumArgumentCharacters
                ? "command_line.argument_too_long"
                : "command_line.too_long";
        }
        else if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal))
        {
            safeErrorCode = "command_line.option_value_missing";
        }

        return true;
    }

    private static bool TryAccumulate(string value, ref int totalCharacters)
    {
        if (value.Length > MaximumArgumentCharacters)
        {
            return false;
        }

        totalCharacters += value.Length;
        return totalCharacters <= MaximumTotalCharacters;
    }

    private static void AddUnique(ICollection<ActivationCommand> commands, ActivationCommand command)
    {
        if (!commands.Any(existing =>
                string.Equals(existing.Name, command.Name, StringComparison.Ordinal)
                && string.Equals(existing.Value, command.Value, StringComparison.Ordinal)))
        {
            commands.Add(command);
        }
    }

    private static AppLaunchRequest Invalid(string safeErrorCode) =>
        new(false, false, [], safeErrorCode, 2);
}
