using System.Text.RegularExpressions;

namespace CodexUsageMonitor.Core.Diagnostics;

public static partial class SafeDiagnosticRedactor
{
    private const int MaximumOutputCharacters = 4096;

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var result = value;
        result = BearerToken().Replace(result, "$1<redacted>");
        result = JsonSecret().Replace(result, "$1<redacted>$3");
        result = EmailAddress().Replace(result, "<email>");
        result = UserProfilePath().Replace(result, "$1\\<user>\\");
        result = LongEncodedValue().Replace(result, "<encoded-value>");
        result = UrlQuerySecret().Replace(result, "$1=<redacted>");
        return result[..Math.Min(result.Length, MaximumOutputCharacters)];
    }

    [GeneratedRegex(@"(?i)\b(Bearer\s+)[A-Za-z0-9._~+\-/]+=*")]
    private static partial Regex BearerToken();

    [GeneratedRegex("(?i)(\\\"?(?:access_token|refresh_token|client_secret|password|authorization)\\\"?\\s*[:=]\\s*\\\"?)([^\\\"\\s,;}]+)(\\\"?)")]
    private static partial Regex JsonSecret();

    [GeneratedRegex(@"(?i)\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b")]
    private static partial Regex EmailAddress();

    [GeneratedRegex(@"(?i)([A-Z]:\\Users)\\[^\\\r\n]+\\")]
    private static partial Regex UserProfilePath();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Za-z0-9+/=_-]{80,}(?![A-Za-z0-9])")]
    private static partial Regex LongEncodedValue();

    [GeneratedRegex(@"(?i)((?:token|code|secret|key|state))=([^&\s]+)")]
    private static partial Regex UrlQuerySecret();
}
