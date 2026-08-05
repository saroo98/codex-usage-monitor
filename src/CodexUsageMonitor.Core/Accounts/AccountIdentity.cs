using System.Security.Cryptography;
using System.Text;

namespace CodexUsageMonitor.Core.Accounts;

public sealed record AccountIdentity
{
    public AccountIdentity(string stableId, string? email, string? displayName, string? organization)
    {
        StableId = NormalizeRequired(stableId, 256, nameof(stableId));
        Email = NormalizeOptional(email, 320);
        DisplayName = NormalizeOptional(displayName, 160);
        Organization = NormalizeOptional(organization, 160);
    }

    public string StableId { get; }

    public string? Email { get; }

    public string? DisplayName { get; }

    public string? Organization { get; }

    public string StorageKey => ComputeStorageKey(StableId);

    public string SafeLabel => DisplayName ?? MaskEmail(Email) ?? "Codex account";

    public static string ComputeStorageKey(string stableId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableId);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(stableId.Trim()));
        return Convert.ToHexStringLower(digest.AsSpan(0, 16));
    }

    private static string? MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        var at = email.IndexOf('@');
        return at <= 0 ? "••••" : $"{email[0]}•••{email[at..]}";
    }

    private static string NormalizeRequired(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : throw new ArgumentOutOfRangeException(parameterName);
    }

    private static string? NormalizeOptional(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : throw new ArgumentOutOfRangeException(nameof(value));
    }
}
