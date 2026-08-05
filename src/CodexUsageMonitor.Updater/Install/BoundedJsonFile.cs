using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexUsageMonitor.Updater.Install;

internal static class BoundedJsonFile
{
    private const int MaximumJsonDepth = 32;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = false,
        MaxDepth = MaximumJsonDepth,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
    };

    public static async Task<T> ReadAsync<T>(
        string path,
        int maximumBytes,
        string safeFailureMessage,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ValidateMaximumBytes(maximumBytes);
        var fullPath = UpdatePathLayout.NormalizePath(path);
        UpdatePathSecurity.EnsureRegularFile(fullPath, safeFailureMessage);
        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 || stream.Length > maximumBytes)
        {
            throw new InvalidDataException(safeFailureMessage);
        }

        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateNoDuplicateProperties(bytes);
            return JsonSerializer.Deserialize<T>(bytes, SerializerOptions)
                ?? throw new InvalidDataException(safeFailureMessage);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or OverflowException)
        {
            throw new InvalidDataException(safeFailureMessage, exception);
        }
    }

    public static async Task WriteAsync<T>(
        string path,
        T value,
        int maximumBytes,
        bool overwrite,
        string safeFailureMessage,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(value);
        ValidateMaximumBytes(maximumBytes);

        byte[] bytes;
        try
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new InvalidDataException(safeFailureMessage, exception);
        }

        if (bytes.Length is <= 0 || bytes.Length > maximumBytes)
        {
            throw new InvalidDataException(safeFailureMessage);
        }

        var fullPath = UpdatePathLayout.NormalizePath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException(safeFailureMessage);
        Directory.CreateDirectory(directory);
        UpdatePathSecurity.EnsureDirectory(directory, safeFailureMessage);
        if (UpdatePathSecurity.PathEntryExists(fullPath))
        {
            UpdatePathSecurity.EnsureRegularFile(fullPath, safeFailureMessage);
        }

        var temporaryPath = fullPath + $".{Guid.NewGuid():N}.tmp";
        UpdatePathSecurity.EnsureDirectChild(
            temporaryPath,
            directory,
            Path.GetFileName(temporaryPath),
            safeFailureMessage);
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, fullPath, overwrite);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static void ValidateNoDuplicateProperties(byte[] bytes)
    {
        using var document = JsonDocument.Parse(
            bytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumJsonDepth,
            });
        ValidateNoDuplicateProperties(document.RootElement);
    }

    private static void ValidateNoDuplicateProperties(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        throw new JsonException("Duplicate JSON object members are not allowed.");
                    }

                    ValidateNoDuplicateProperties(property.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    ValidateNoDuplicateProperties(item);
                }

                break;
        }
    }

    private static void ValidateMaximumBytes(int maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (UpdatePathSecurity.PathEntryExists(path))
            {
                UpdatePathSecurity.EnsureRegularFile(path, "The temporary update metadata file is invalid.");
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
        }
    }
}
