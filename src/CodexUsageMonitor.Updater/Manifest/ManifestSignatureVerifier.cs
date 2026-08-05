using System.Security.Cryptography;
using NSec.Cryptography;

namespace CodexUsageMonitor.Updater.Manifest;

public sealed class ManifestSignatureVerifier
{
    private readonly byte[] _publicKey;

    public ManifestSignatureVerifier(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != 32)
        {
            throw new ArgumentException("Ed25519 public keys must contain 32 bytes.", nameof(publicKey));
        }

        _publicKey = publicKey.ToArray();
    }

    public bool Verify(UpdateManifestDocument manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(manifest.Signature);
        }
        catch (FormatException)
        {
            return false;
        }

        var payload = CanonicalManifestWriter.WriteSignedPayload(manifest);
        try
        {
            if (signature.Length != 64)
            {
                return false;
            }

            var algorithm = SignatureAlgorithm.Ed25519;
            var key = PublicKey.Import(algorithm, _publicKey, KeyBlobFormat.RawPublicKey);
            return algorithm.Verify(key, payload, signature);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(signature);
        }
    }
}
