using System.Text.Json.Serialization;
using CodexUsageMonitor.Updater.Model;

namespace CodexUsageMonitor.Updater.Manifest;

public enum UpdateArchitecture
{
    X64,
    Arm64,
}

public sealed record UpdateAsset(
    [property: JsonPropertyName("architecture")] string Architecture,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("publisherThumbprints")] IReadOnlyList<string> PublisherThumbprints);

public sealed record UpdateManifestDocument(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("publishedAtUtc")] DateTimeOffset PublishedAtUtc,
    [property: JsonPropertyName("minimumOsBuild")] int MinimumOsBuild,
    [property: JsonPropertyName("releaseNotesUrl")] string ReleaseNotesUrl,
    [property: JsonPropertyName("assets")] IReadOnlyList<UpdateAsset> Assets,
    [property: JsonPropertyName("signature")] string Signature)
{
    public const int CurrentSchemaVersion = 1;

    public SemanticVersion ParsedVersion => SemanticVersion.Parse(Version);

    public UpdateAsset SelectAsset(UpdateArchitecture architecture)
    {
        var name = architecture is UpdateArchitecture.Arm64 ? "arm64" : "x64";
        return Assets.Single(asset => string.Equals(asset.Architecture, name, StringComparison.OrdinalIgnoreCase));
    }
}
