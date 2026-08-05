using System.IO.Compression;
using System.Text.RegularExpressions;

namespace CodexUsageMonitor.Persistence.Diagnostics;

public static partial class SupportBundleSecretScanner
{
    public static void AssertSafe(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (entry.Length > 8L * 1024 * 1024 || !IsText(entry.FullName))
            {
                continue;
            }

            using var reader = new StreamReader(entry.Open(), detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            if (Bearer().IsMatch(text) || RefreshToken().IsMatch(text) || Email().IsMatch(text) || PrivateKey().IsMatch(text))
            {
                throw new InvalidDataException($"Support bundle safety scan rejected {entry.FullName}.");
            }
        }
    }

    private static bool IsText(string name) =>
        name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".md", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{16,}")]
    private static partial Regex Bearer();

    [GeneratedRegex("""(?i)"?refresh_token"?\s*[:=]\s*"?(?!<redacted>)[^"\s,}]{8,}""")]
    private static partial Regex RefreshToken();

    [GeneratedRegex(@"(?i)\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b")]
    private static partial Regex Email();

    [GeneratedRegex("-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----")]
    private static partial Regex PrivateKey();
}
