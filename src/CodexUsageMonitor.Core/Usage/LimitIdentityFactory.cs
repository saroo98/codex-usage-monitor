using System.Security.Cryptography;
using System.Text;

namespace CodexUsageMonitor.Core.Usage;

public sealed record LimitIdentityInput(
    string? ServerId,
    LimitKind Kind,
    string? Model,
    long? WindowSeconds,
    string? Scope);

public static class LimitIdentityFactory
{
    public static string Create(LimitIdentityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!string.IsNullOrWhiteSpace(input.ServerId))
        {
            return "server:" + Normalize(input.ServerId);
        }

        var material = string.Join('|',
            input.Kind.ToString(),
            Normalize(input.Model),
            input.WindowSeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            Normalize(input.Scope));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return "derived:" + Convert.ToHexStringLower(digest.AsSpan(0, 16));
    }

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;
}
