using System.Text.Json;

namespace CodexUsageMonitor.Updater.Manifest;

public static class CanonicalManifestWriter
{
    public static byte[] WriteSignedPayload(UpdateManifestDocument manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", manifest.SchemaVersion);
            writer.WriteString("channel", manifest.Channel);
            writer.WriteString("version", manifest.Version);
            writer.WriteString("publishedAtUtc", manifest.PublishedAtUtc.ToUniversalTime());
            writer.WriteNumber("minimumOsBuild", manifest.MinimumOsBuild);
            writer.WriteString("releaseNotesUrl", manifest.ReleaseNotesUrl);
            writer.WritePropertyName("assets");
            writer.WriteStartArray();
            foreach (var asset in manifest.Assets.OrderBy(static item => item.Architecture, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("architecture", asset.Architecture);
                writer.WriteString("url", asset.Url);
                writer.WriteString("fileName", asset.FileName);
                writer.WriteNumber("sizeBytes", asset.SizeBytes);
                writer.WriteString("sha256", asset.Sha256.ToLowerInvariant());
                writer.WritePropertyName("publisherThumbprints");
                writer.WriteStartArray();
                foreach (var thumbprint in asset.PublisherThumbprints
                    .Select(NormalizeThumbprint)
                    .Order(StringComparer.Ordinal))
                {
                    writer.WriteStringValue(thumbprint);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static string NormalizeThumbprint(string value) =>
        string.Concat(value.Where(char.IsAsciiHexDigit)).ToUpperInvariant();
}
