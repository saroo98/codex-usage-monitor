using System.IO;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CodexUsageMonitor.Windows.Runtime;

public static class ActivationCommandNames
{
    public const string ShowWidget = "show-widget";
    public const string HideWidget = "hide-widget";
    public const string OpenSettings = "open-settings";
    public const string OpenDiagnostics = "open-diagnostics";
    public const string Refresh = "refresh";
    public const string DisableClickThrough = "disable-click-through";
    public const string ReviewResetCredit = "review-reset-credit";
    public const string UpdateHealth = "update-health";
    public const string UpdateRolledBack = "update-rolled-back";
    public const string Exit = "exit";

    public static bool IsKnown(string name) => name is
        ShowWidget or
        HideWidget or
        OpenSettings or
        OpenDiagnostics or
        Refresh or
        DisableClickThrough or
        ReviewResetCredit or
        UpdateHealth or
        UpdateRolledBack or
        Exit;
}

public sealed record ActivationCommand(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] string? Value = null)
{
    public const int MaximumNameCharacters = 48;
    public const int MaximumValueCharacters = 4096;

    public bool IsStructurallyValid() =>
        !string.IsNullOrWhiteSpace(Name)
        && Name.Length <= MaximumNameCharacters
        && Name.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-');
}

public sealed record StartupHealthRequest(Guid TransactionId, string HealthMarkerPath)
{
    private const char Separator = ':';

    public string Encode()
    {
        if (TransactionId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(TransactionId));
        }

        ValidateHealthMarkerPath(HealthMarkerPath);
        var pathBytes = Encoding.UTF8.GetBytes(HealthMarkerPath);
        var encodedPath = Convert.ToBase64String(pathBytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"{TransactionId:D}{Separator}{encodedPath}";
    }

    public static bool TryDecode(string? value, out StartupHealthRequest request)
    {
        request = default!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > ActivationCommand.MaximumValueCharacters)
        {
            return false;
        }

        var separator = value.IndexOf(Separator, StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 1 ||
            !Guid.TryParse(value[..separator], out var transactionId) || transactionId == Guid.Empty)
        {
            return false;
        }

        try
        {
            var encodedPath = value[(separator + 1)..].Replace('-', '+').Replace('_', '/');
            encodedPath = encodedPath.PadRight(encodedPath.Length + ((4 - encodedPath.Length % 4) % 4), '=');
            var healthMarkerPath = Encoding.UTF8.GetString(Convert.FromBase64String(encodedPath));
            ValidateHealthMarkerPath(healthMarkerPath);
            request = new StartupHealthRequest(transactionId, healthMarkerPath);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or DecoderFallbackException)
        {
            return false;
        }
    }

    private static void ValidateHealthMarkerPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Length > 2048 || path.IndexOfAny(['\0', '\r', '\n']) >= 0 || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The startup health marker path is invalid.", nameof(path));
        }
    }
}

public sealed record ActivationMessage(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("commands")] IReadOnlyList<ActivationCommand> Commands)
{
    public const int CurrentVersion = 1;
    public const int MaximumCommands = 16;

    private static readonly Regex SettingsSectionPattern = new(
        "^[A-Za-z][A-Za-z0-9]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public bool TryValidate(out string? safeErrorCode)
    {
        if (Version != CurrentVersion)
        {
            safeErrorCode = "activation.unsupported_version";
            return false;
        }

        if (Commands is null || Commands.Count is <= 0 or > MaximumCommands)
        {
            safeErrorCode = "activation.invalid_command_count";
            return false;
        }

        foreach (var command in Commands)
        {
            if (command is null || !command.IsStructurallyValid() || !ActivationCommandNames.IsKnown(command.Name))
            {
                safeErrorCode = "activation.invalid_command";
                return false;
            }

            if (command.Value is { Length: > ActivationCommand.MaximumValueCharacters } || !ValidateValue(command))
            {
                safeErrorCode = "activation.invalid_value";
                return false;
            }
        }

        safeErrorCode = null;
        return true;
    }

    private static bool ValidateValue(ActivationCommand command) => command.Name switch
    {
        ActivationCommandNames.OpenSettings =>
            string.IsNullOrEmpty(command.Value) || SettingsSectionPattern.IsMatch(command.Value),
        ActivationCommandNames.ReviewResetCredit =>
            Guid.TryParse(command.Value, out var profileId) && profileId != Guid.Empty,
        ActivationCommandNames.UpdateHealth =>
            StartupHealthRequest.TryDecode(command.Value, out _),
        ActivationCommandNames.UpdateRolledBack =>
            Guid.TryParse(command.Value, out var transactionId) && transactionId != Guid.Empty,
        _ => string.IsNullOrEmpty(command.Value),
    };
}
