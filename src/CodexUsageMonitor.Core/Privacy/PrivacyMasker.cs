using System.Text.RegularExpressions;

namespace CodexUsageMonitor.Core.Privacy;

public static partial class PrivacyMasker
{
    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var redacted = BearerRegex().Replace(value, "$1[REDACTED]");
        redacted = EmailRegex().Replace(redacted, static match => MaskEmail(match.Value));
        redacted = WindowsUserPathRegex().Replace(redacted, "$1[USER]$3");
        redacted = JsonSecretRegex().Replace(redacted, "$1\"[REDACTED]\"");
        return redacted;
    }

    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        return at <= 0 ? "[EMAIL]" : $"{email[0]}•••{email[at..]}";
    }

    [GeneratedRegex("(?i)(bearer\\s+|authorization[=:]\\s*)[A-Za-z0-9._~+/-]{12,}={0,2}", RegexOptions.CultureInvariant)]
    private static partial Regex BearerRegex();

    [GeneratedRegex("(?i)\\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\\.[A-Z]{2,}\\b", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex("(?i)([A-Z]:\\\\Users\\\\)([^\\\\]+)(\\\\)", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsUserPathRegex();

    [GeneratedRegex("(?i)(\"(?:password|token|secret|refresh_token|access_token|client_secret)\"\\s*:\\s*)\"[^\"]*\"", RegexOptions.CultureInvariant)]
    private static partial Regex JsonSecretRegex();
}
