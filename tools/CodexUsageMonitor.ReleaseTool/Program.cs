using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexUsageMonitor.Updater.Manifest;
using NSec.Cryptography;

return await ReleaseManifestCommand.RunAsync(args);

internal static class ReleaseManifestCommand
{
    private const string Usage = "Usage: sign --manifest <path> --trust-anchor <base64> (--private-key <path> | --private-key-env <name>) | verify --manifest <path> --trust-anchor <base64> | validate-keypair --trust-anchor <base64> (--private-key <path> | --private-key-env <name>) | generate-keypair --private-key-output <path> --public-key-output <path>";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = Parse(args);
            switch (options.Operation)
            {
                case "sign":
                    await SignAsync(options).ConfigureAwait(false);
                    Console.WriteLine($"Update manifest sign succeeded: {options.ManifestPath}");
                    break;
                case "verify":
                    await VerifyAsync(options).ConfigureAwait(false);
                    Console.WriteLine($"Update manifest verify succeeded: {options.ManifestPath}");
                    break;
                case "validate-keypair":
                    await ValidateKeypairAsync(options).ConfigureAwait(false);
                    Console.WriteLine("Update signing keypair validation succeeded.");
                    break;
                case "generate-keypair":
                    await GenerateKeypairAsync(options).ConfigureAwait(false);
                    break;
                default:
                    throw new UnreachableException();
            }

            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or JsonException or CryptographicException or InvalidDataException or FormatException or NotSupportedException or InvalidOperationException)
        {
            Console.Error.WriteLine($"release-manifest: {exception.Message}");
            return 2;
        }
    }

    private static async Task SignAsync(CommandOptions options)
    {
        var manifest = await ReadManifestAsync(options.ManifestPath!).ConfigureAwait(false);
        var payload = CanonicalManifestWriter.WriteSignedPayload(manifest);
        await UsePrivateKeyAsync(options, async privateKey =>
        {
            using var key = Key.Import(SignatureAlgorithm.Ed25519, privateKey, KeyBlobFormat.RawPrivateKey);
            EnsureKeyMatchesTrustAnchor(key, options.TrustAnchor!);
            var signature = SignatureAlgorithm.Ed25519.Sign(key, payload);
            if (signature.Length != 64 || !SignatureAlgorithm.Ed25519.Verify(key.PublicKey, payload, signature))
            {
                throw new CryptographicException("The generated update manifest signature could not be verified.");
            }

            var signed = manifest with { Signature = Convert.ToBase64String(signature) };
            await File.WriteAllTextAsync(
                options.ManifestPath!,
                JsonSerializer.Serialize(signed, JsonOptions),
                Utf8WithoutBom).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private static async Task VerifyAsync(CommandOptions options)
    {
        var manifest = await ReadManifestAsync(options.ManifestPath!).ConfigureAwait(false);
        var payload = CanonicalManifestWriter.WriteSignedPayload(manifest);
        var publicKeyBytes = DecodeTrustAnchor(options.TrustAnchor!);
        var signature = Convert.FromBase64String(manifest.Signature);
        if (signature.Length != 64)
        {
            throw new CryptographicException("Update manifest signature is invalid.");
        }

        var publicKey = PublicKey.Import(SignatureAlgorithm.Ed25519, publicKeyBytes, KeyBlobFormat.RawPublicKey);
        if (!SignatureAlgorithm.Ed25519.Verify(publicKey, payload, signature))
        {
            throw new CryptographicException("Update manifest signature is invalid.");
        }
    }

    private static Task ValidateKeypairAsync(CommandOptions options) =>
        UsePrivateKeyAsync(options, privateKey =>
        {
            using var key = Key.Import(SignatureAlgorithm.Ed25519, privateKey, KeyBlobFormat.RawPrivateKey);
            EnsureKeyMatchesTrustAnchor(key, options.TrustAnchor!);
            return Task.CompletedTask;
        });

    private static async Task GenerateKeypairAsync(CommandOptions options)
    {
        var privateKeyPath = options.PrivateKeyOutputPath!;
        var publicKeyPath = options.PublicKeyOutputPath!;
        EnsureOutputDoesNotExist(privateKeyPath);
        EnsureOutputDoesNotExist(publicKeyPath);

        byte[]? privateKeyBytes = null;
        var privateOutputCreated = false;
        var publicOutputCreated = false;
        try
        {
            using var key = Key.Create(
                SignatureAlgorithm.Ed25519,
                new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextArchiving });
            privateKeyBytes = key.Export(KeyBlobFormat.RawPrivateKey);
            var publicKeyBytes = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
            if (privateKeyBytes.Length != 32 || publicKeyBytes.Length != 32)
            {
                throw new CryptographicException("NSec produced an unexpected Ed25519 key size.");
            }

            await using (var privateOutput = OpenNewOutput(privateKeyPath))
            {
                privateOutputCreated = true;
                await privateOutput.WriteAsync(privateKeyBytes).ConfigureAwait(false);
            }

            var trustAnchor = Convert.ToBase64String(publicKeyBytes);
            await using (var publicOutput = OpenNewOutput(publicKeyPath))
            {
                publicOutputCreated = true;
                await using var writer = new StreamWriter(publicOutput, Utf8WithoutBom);
                await writer.WriteLineAsync(trustAnchor).ConfigureAwait(false);
            }

            Console.WriteLine($"Private key output: {privateKeyPath}");
            Console.WriteLine($"Public key output: {publicKeyPath}");
            Console.WriteLine($"UPDATE_TRUST_ANCHOR={trustAnchor}");
        }
        catch (Exception failure)
        {
            var cleanupFailures = new List<Exception>();
            DeleteCreatedOutput(publicKeyPath, publicOutputCreated, cleanupFailures);
            DeleteCreatedOutput(privateKeyPath, privateOutputCreated, cleanupFailures);
            if (cleanupFailures.Count > 0)
            {
                cleanupFailures.Insert(0, failure);
                throw new IOException("Key generation failed and newly created output cleanup also failed.", new AggregateException(cleanupFailures));
            }

            ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
        finally
        {
            if (privateKeyBytes is not null)
            {
                CryptographicOperations.ZeroMemory(privateKeyBytes);
            }
        }
    }

    private static async Task<UpdateManifestDocument> ReadManifestAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<UpdateManifestDocument>(stream, JsonOptions).ConfigureAwait(false)
            ?? throw new InvalidDataException("Update manifest is empty.");
    }

    private static async Task UsePrivateKeyAsync(CommandOptions options, Func<byte[], Task> action)
    {
        byte[]? privateKey = null;
        try
        {
            privateKey = await ReadKeyAsync(options).ConfigureAwait(false);
            await action(privateKey).ConfigureAwait(false);
        }
        finally
        {
            if (privateKey is not null)
            {
                CryptographicOperations.ZeroMemory(privateKey);
            }
        }
    }

    private static Task<byte[]> ReadKeyAsync(CommandOptions options) =>
        options.PrivateKeyPath is not null
            ? ReadFileKeyAsync(options.PrivateKeyPath)
            : Task.FromResult(ReadEnvironmentKey(options.PrivateKeyEnvironmentName!));

    private static async Task<byte[]> ReadFileKeyAsync(string path)
    {
        var raw = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        if (raw.Length == 32)
        {
            return raw;
        }

        byte[]? decoded = null;
        try
        {
            decoded = Convert.FromBase64String(Encoding.UTF8.GetString(raw).Trim());
            if (decoded.Length == 32)
            {
                return decoded;
            }
        }
        catch (FormatException)
        {
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
            if (decoded is not null && decoded.Length != 32)
            {
                CryptographicOperations.ZeroMemory(decoded);
            }
        }

        throw new InvalidDataException("The Ed25519 private key must contain exactly 32 raw bytes or base64-decode to exactly 32 bytes.");
    }

    private static byte[] ReadEnvironmentKey(string environmentVariableName)
    {
        string? encodedKey = Environment.GetEnvironmentVariable(environmentVariableName);
        Environment.SetEnvironmentVariable(environmentVariableName, null);
        byte[]? decoded = null;
        var returned = false;
        try
        {
            if (encodedKey is null)
            {
                throw new InvalidDataException($"Environment variable '{environmentVariableName}' is not set.");
            }

            decoded = Convert.FromBase64String(encodedKey);
            encodedKey = null;
            if (decoded.Length != 32)
            {
                throw new InvalidDataException("The Ed25519 private key environment value must base64-decode to exactly 32 bytes.");
            }

            returned = true;
            return decoded;
        }
        finally
        {
            encodedKey = null;
            if (!returned && decoded is not null)
            {
                CryptographicOperations.ZeroMemory(decoded);
            }
        }
    }

    private static void EnsureKeyMatchesTrustAnchor(Key key, string trustAnchor)
    {
        var expectedPublicKey = DecodeTrustAnchor(trustAnchor);
        var actualPublicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        if (!CryptographicOperations.FixedTimeEquals(actualPublicKey, expectedPublicKey))
        {
            throw new CryptographicException("The update signing key does not match the configured trust anchor.");
        }
    }

    private static byte[] DecodeTrustAnchor(string trustAnchor)
    {
        var decoded = Convert.FromBase64String(trustAnchor);
        if (decoded.Length != 32)
        {
            throw new InvalidDataException("The update trust anchor must base64-decode to exactly 32 bytes.");
        }

        return decoded;
    }

    private static FileStream OpenNewOutput(string path)
    {
        try
        {
            return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        }
        catch (IOException exception) when (Path.Exists(path))
        {
            throw new IOException($"Output already exists: {path}", exception);
        }
    }

    private static void EnsureOutputDoesNotExist(string path)
    {
        if (Path.Exists(path))
        {
            throw new IOException($"Output already exists: {path}");
        }
    }

    private static void DeleteCreatedOutput(string path, bool created, ICollection<Exception> failures)
    {
        if (!created)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failures.Add(exception);
        }
    }

    private static CommandOptions Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("sign" or "verify" or "validate-keypair" or "generate-keypair"))
        {
            throw new ArgumentException(Usage);
        }

        var values = ParseNamedValues(args);
        return args[0] switch
        {
            "sign" => ParseSign(values),
            "verify" => ParseVerify(values),
            "validate-keypair" => ParseValidateKeypair(values),
            "generate-keypair" => ParseGenerateKeypair(values),
            _ => throw new UnreachableException(),
        };
    }

    private static Dictionary<string, string> ParseNamedValues(string[] args)
    {
        if ((args.Length - 1) % 2 != 0)
        {
            throw new ArgumentException("Release manifest arguments must be named key/value pairs.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) ||
                args[index].Length == 2 ||
                args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Release manifest arguments must be named key/value pairs.");
            }

            if (!values.TryAdd(args[index][2..], args[index + 1]))
            {
                throw new ArgumentException($"Duplicate argument: {args[index]}");
            }
        }

        return values;
    }

    private static CommandOptions ParseSign(IReadOnlyDictionary<string, string> values)
    {
        RejectUnknownArguments(values, "manifest", "trust-anchor", "private-key", "private-key-env");
        var keySource = ParsePrivateKeySource(values, "sign");
        return new CommandOptions(
            "sign",
            Path.GetFullPath(GetRequired(values, "manifest")),
            GetRequired(values, "trust-anchor"),
            keySource.Path,
            keySource.EnvironmentName,
            null,
            null);
    }

    private static CommandOptions ParseVerify(IReadOnlyDictionary<string, string> values)
    {
        if (values.ContainsKey("private-key") || values.ContainsKey("private-key-env"))
        {
            throw new ArgumentException("The verify operation does not accept a private key source.");
        }

        RejectUnknownArguments(values, "manifest", "trust-anchor");
        return new CommandOptions(
            "verify",
            Path.GetFullPath(GetRequired(values, "manifest")),
            GetRequired(values, "trust-anchor"),
            null,
            null,
            null,
            null);
    }

    private static CommandOptions ParseValidateKeypair(IReadOnlyDictionary<string, string> values)
    {
        RejectUnknownArguments(values, "trust-anchor", "private-key", "private-key-env");
        var keySource = ParsePrivateKeySource(values, "validate-keypair");
        return new CommandOptions(
            "validate-keypair",
            null,
            GetRequired(values, "trust-anchor"),
            keySource.Path,
            keySource.EnvironmentName,
            null,
            null);
    }

    private static CommandOptions ParseGenerateKeypair(IReadOnlyDictionary<string, string> values)
    {
        RejectUnknownArguments(values, "private-key-output", "public-key-output");
        return new CommandOptions(
            "generate-keypair",
            null,
            null,
            null,
            null,
            Path.GetFullPath(GetRequired(values, "private-key-output")),
            Path.GetFullPath(GetRequired(values, "public-key-output")));
    }

    private static (string? Path, string? EnvironmentName) ParsePrivateKeySource(
        IReadOnlyDictionary<string, string> values,
        string operation)
    {
        var hasPath = values.TryGetValue("private-key", out var path);
        var hasEnvironment = values.TryGetValue("private-key-env", out var environmentName);
        if (hasPath && hasEnvironment)
        {
            throw new ArgumentException("The --private-key and --private-key-env arguments are mutually exclusive.");
        }

        if (!hasPath && !hasEnvironment)
        {
            throw new ArgumentException($"The {operation} operation requires exactly one private key source.");
        }

        return hasPath
            ? (System.IO.Path.GetFullPath(RequireNonempty(path!, "private-key")), null)
            : (null, RequireNonempty(environmentName!, "private-key-env"));
    }

    private static string GetRequired(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value)
            ? RequireNonempty(value, name)
            : throw new ArgumentException($"Required argument is missing: --{name}");

    private static string RequireNonempty(string value, string name) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Argument --{name} must not be empty.");

    private static void RejectUnknownArguments(IReadOnlyDictionary<string, string> values, params string[] allowed)
    {
        var allowedNames = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var name in values.Keys)
        {
            if (!allowedNames.Contains(name))
            {
                throw new ArgumentException($"Unknown argument for this operation: --{name}");
            }
        }
    }

    private sealed record CommandOptions(
        string Operation,
        string? ManifestPath,
        string? TrustAnchor,
        string? PrivateKeyPath,
        string? PrivateKeyEnvironmentName,
        string? PrivateKeyOutputPath,
        string? PublicKeyOutputPath);
}
