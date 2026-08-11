using System.Security.Cryptography;
using CodexUsageMonitor.Updater.Install;
using CodexUsageMonitor.Updater.Model;
using CodexUsageMonitor.Updater.Staging;

namespace CodexUsageMonitor.Updater.Manifest;

public sealed class UpdateManifestValidator
{
    private const int MaximumUrlCharacters = 2048;
    private const int MaximumFileNameCharacters = 160;
    private static readonly HashSet<string> AcceptedChannels = new(StringComparer.Ordinal)
    {
        "preview",
        "stable",
    };
    private readonly UpdateTrustPolicyOptions _trustOptions;

    public UpdateManifestValidator(UpdateTrustPolicyOptions? trustOptions = null)
    {
        _trustOptions = trustOptions ?? UpdateTrustPolicyOptions.Production;
    }

    public void Validate(UpdateManifestDocument manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion != UpdateManifestDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException("Update manifest schema is unsupported.");
        }

        if (manifest.Channel is null || !AcceptedChannels.Contains(manifest.Channel))
        {
            throw new InvalidDataException("Update channel is invalid.");
        }

        if (!SemanticVersion.TryParse(manifest.Version, out var parsedVersion) ||
            !string.Equals(parsedVersion.ToString(), manifest.Version, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Update version is invalid.");
        }

        if (manifest.PublishedAtUtc == default ||
            manifest.PublishedAtUtc > DateTimeOffset.UtcNow.AddDays(2))
        {
            throw new InvalidDataException("Update publication time is invalid.");
        }

        if (manifest.MinimumOsBuild is < 19041 or > 1_000_000)
        {
            throw new InvalidDataException("Minimum Windows build is invalid.");
        }

        ValidateHttps(manifest.ReleaseNotesUrl, "release notes");
        if (manifest.Assets is null || manifest.Assets.Count is <= 0 or > 4)
        {
            throw new InvalidDataException("Update manifest asset count is invalid.");
        }

        var architectures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in manifest.Assets)
        {
            if (asset is null)
            {
                throw new InvalidDataException("Update manifest contains an empty asset.");
            }

            var supportedArchitecture = asset.Architecture is "arm64" or "x64";
            if (!supportedArchitecture || !architectures.Add(asset.Architecture))
            {
                throw new InvalidDataException("Update asset architecture is invalid or duplicated.");
            }

            ValidateHttps(asset.Url, "asset");
            if (!IsSafeWindowsFileName(asset.FileName))
            {
                throw new InvalidDataException("Update asset file name is invalid.");
            }

            if (asset.SizeBytes is <= 0 or > 512L * 1024 * 1024)
            {
                throw new InvalidDataException("Update asset size is invalid.");
            }

            if (!IsCanonicalSha256(asset.Sha256))
            {
                throw new InvalidDataException("Update asset digest is invalid.");
            }

            if (asset.PublisherThumbprints is null)
            {
                throw new InvalidDataException("Update asset publisher pins are invalid.");
            }

            var allowEmptyPins = UpdateBuildIdentity.IsPublicUnsignedRelease ||
                (_trustOptions.AllowUnsignedDevelopmentArtifacts && UpdateBuildIdentity.IsDevelopmentBuild);
            UpdatePublisherPins.ValidateCanonical(asset.PublisherThumbprints, allowEmptyPins);
        }

        if (!architectures.SetEquals(["arm64", "x64"]))
        {
            throw new InvalidDataException("Update manifest must include x64 and arm64 assets.");
        }

        ValidateSignature(manifest.Signature);
    }

    private static void ValidateHttps(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaximumUrlCharacters ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException($"Update {field} URL must use HTTPS without credentials or fragments.");
        }
    }

    private static bool IsSafeWindowsFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaximumFileNameCharacters ||
            value is "." or ".." ||
            value.EndsWith(' ') ||
            value.EndsWith('.') ||
            value.Contains('/') ||
            value.Contains('\\') ||
            value.Any(static character =>
                char.IsControl(character) || character is ':' or '*' or '?' or '"' or '<' or '>' or '|'))
        {
            return false;
        }

        var baseName = value.Split('.', 2, StringSplitOptions.None)[0];
        return !baseName.Equals("CON", StringComparison.OrdinalIgnoreCase) &&
            !baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase) &&
            !baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase) &&
            !baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase) &&
            !IsNumberedDevice(baseName, "COM") &&
            !IsNumberedDevice(baseName, "LPT");
    }

    private static bool IsNumberedDevice(string value, string prefix) =>
        value.Length == 4 &&
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        value[3] is >= '1' and <= '9';

    private static bool IsCanonicalSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(static character =>
            char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');

    private static void ValidateSignature(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            throw new InvalidDataException("Update signature is invalid.");
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(value);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Update signature is invalid.", exception);
        }

        try
        {
            if (signature.Length != 64)
            {
                throw new InvalidDataException("Update signature length is invalid.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }
}
