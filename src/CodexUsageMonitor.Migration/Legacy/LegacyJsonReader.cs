using System.Text.Json;

namespace CodexUsageMonitor.Migration.Legacy;

public sealed class LegacyJsonReader
{
    private const int MaximumJsonBytes = 2 * 1024 * 1024;

    public async Task<JsonElement?> ReadObjectAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaximumJsonBytes)
        {
            throw new InvalidDataException("Legacy JSON file size is invalid.");
        }

        using var document = await JsonDocument.ParseAsync(
            stream,
            new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 },
            cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind is not JsonValueKind.Object)
        {
            throw new InvalidDataException("Legacy JSON root must be an object.");
        }

        return document.RootElement.Clone();
    }
}
