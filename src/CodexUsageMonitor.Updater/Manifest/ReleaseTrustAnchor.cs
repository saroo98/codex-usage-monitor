using System.Reflection;
using System.Security.Cryptography;

namespace CodexUsageMonitor.Updater.Manifest;

public static class ReleaseTrustAnchor
{
    public static byte[] Load()
    {
        var encoded = typeof(ReleaseTrustAnchor).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(static attribute => attribute.Key == "UpdatePublicKeyBase64")
            ?.Value;
        if (string.IsNullOrWhiteSpace(encoded))
        {
            throw new CryptographicException("The update trust anchor is not embedded in this build.");
        }

        try
        {
            var key = Convert.FromBase64String(encoded);
            return key.Length == 32
                ? key
                : throw new CryptographicException("The embedded update trust anchor is invalid.");
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("The embedded update trust anchor is invalid.", exception);
        }
    }
}
