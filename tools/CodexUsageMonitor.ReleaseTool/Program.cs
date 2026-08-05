using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexUsageMonitor.Updater.Manifest;
using NSec.Cryptography;

return await ReleaseManifestCommand.RunAsync(args);

internal static class ReleaseManifestCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = Parse(args);
            var manifest = await ReadManifestAsync(options.ManifestPath).ConfigureAwait(false);
            var payload = CanonicalManifestWriter.WriteSignedPayload(manifest);
            if (options.Operation is "sign")
            {
                var privateKey = await ReadKeyAsync(options.PrivateKeyPath!).ConfigureAwait(false);
                try
                {
                    using var key = Key.Import(SignatureAlgorithm.Ed25519, privateKey, KeyBlobFormat.RawPrivateKey);
                    var expectedPublicKey = Convert.FromBase64String(options.TrustAnchor);
                    var actualPublicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
                    if (!CryptographicOperations.FixedTimeEquals(actualPublicKey, expectedPublicKey))
                    {
                        throw new CryptographicException("The update signing key does not match the configured trust anchor.");
                    }

                    var signed = manifest with
                    {
                        Signature = Convert.ToBase64String(SignatureAlgorithm.Ed25519.Sign(key, payload)),
                    };
                    await File.WriteAllTextAsync(
                        options.ManifestPath,
                        JsonSerializer.Serialize(signed, JsonOptions),
                        new UTF8Encoding(false)).ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(privateKey);
                }
            }
            else
            {
                var publicKey = PublicKey.Import(
                    SignatureAlgorithm.Ed25519,
                    Convert.FromBase64String(options.TrustAnchor),
                    KeyBlobFormat.RawPublicKey);
                if (!SignatureAlgorithm.Ed25519.Verify(publicKey, payload, Convert.FromBase64String(manifest.Signature)))
                {
                    throw new CryptographicException("Update manifest signature is invalid.");
                }
            }

            Console.WriteLine($"Update manifest {options.Operation} succeeded: {options.ManifestPath}");
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or JsonException or CryptographicException or InvalidDataException or FormatException)
        {
            Console.Error.WriteLine($"release-manifest: {exception.Message}");
            return 2;
        }
    }

    private static async Task<UpdateManifestDocument> ReadManifestAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<UpdateManifestDocument>(stream, JsonOptions).ConfigureAwait(false)
            ?? throw new InvalidDataException("Update manifest is empty.");
    }

    private static async Task<byte[]> ReadKeyAsync(string path)
    {
        var raw = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        if (raw.Length == 32)
        {
            return raw;
        }

        try
        {
            var decoded = Convert.FromBase64String(Encoding.UTF8.GetString(raw).Trim());
            if (decoded.Length == 32)
            {
                return decoded;
            }
            CryptographicOperations.ZeroMemory(decoded);
        }
        catch (FormatException)
        {
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }

        throw new InvalidDataException("The Ed25519 private key must contain exactly 32 raw bytes.");
    }

    private static CommandOptions Parse(string[] args)
    {
        if (args.Length < 5 || args[0] is not ("sign" or "verify"))
        {
            throw new ArgumentException("Usage: sign|verify --manifest <path> --trust-anchor <base64> [--private-key <path>]");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Release manifest arguments must be named key/value pairs.");
            }
            if (!values.TryAdd(args[index][2..], args[index + 1]))
            {
                throw new ArgumentException($"Duplicate argument: {args[index]}");
            }
        }

        if (!values.TryGetValue("manifest", out var manifest) ||
            !values.TryGetValue("trust-anchor", out var trustAnchor) ||
            (args[0] == "sign" && !values.TryGetValue("private-key", out _)))
        {
            throw new ArgumentException("Required release manifest arguments are missing.");
        }

        return new CommandOptions(
            args[0],
            Path.GetFullPath(manifest),
            trustAnchor,
            values.GetValueOrDefault("private-key") is { } key ? Path.GetFullPath(key) : null);
    }

    private sealed record CommandOptions(
        string Operation,
        string ManifestPath,
        string TrustAnchor,
        string? PrivateKeyPath);
}
