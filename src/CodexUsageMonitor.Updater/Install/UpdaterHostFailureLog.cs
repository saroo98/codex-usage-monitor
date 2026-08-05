using System.Text;

namespace CodexUsageMonitor.Updater.Install;

/// <summary>
/// Writes a bounded, code-only updater-host failure history without recording request data or exception text.
/// </summary>
public static class UpdaterHostFailureLog
{
    public const int MaximumLogBytes = 16 * 1024;
    public const int MaximumLines = 64;
    public const int MaximumLineCharacters = 160;
    public const string RelativeDirectory = "CodexUsageMonitor/UpdaterHost";
    public const string FileName = "last-failure.log";
    public const string UnclassifiedFailureCode = "update.host_unclassified_failure";

    public static void TryWrite(string safeCode)
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            return;
        }

        TryWrite(safeCode, localApplicationData, DateTimeOffset.UtcNow);
    }

    public static void TryWrite(
        string safeCode,
        string localApplicationDataDirectory,
        DateTimeOffset timestampUtc)
    {
        try
        {
            WriteCore(safeCode, localApplicationDataDirectory, timestampUtc);
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            ArgumentException or
            NotSupportedException)
        {
        }
    }

    public static bool IsSafeErrorCode(string? value) =>
        value is { Length: > 0 and <= 96 } &&
        value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    public static string GetLogPath(string localApplicationDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationDataDirectory);
        return Path.Combine(
            UpdatePathLayout.NormalizePath(localApplicationDataDirectory),
            RelativeDirectory.Replace('/', Path.DirectorySeparatorChar),
            FileName);
    }

    private static void WriteCore(
        string safeCode,
        string localApplicationDataDirectory,
        DateTimeOffset timestampUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationDataDirectory);
        safeCode = IsSafeErrorCode(safeCode) ? safeCode : UnclassifiedFailureCode;
        var timestamp = timestampUtc.ToUniversalTime();
        if (timestamp == default || timestamp.Year is < 2000 or > 9998)
        {
            throw new ArgumentOutOfRangeException(nameof(timestampUtc));
        }

        var path = GetLogPath(localApplicationDataDirectory);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidDataException("The updater host log directory is invalid.");
        Directory.CreateDirectory(directory);
        UpdatePathSecurity.EnsureDirectory(
            directory,
            "The updater host log directory is invalid.");
        UpdatePathSecurity.EnsureDirectChild(
            path,
            directory,
            FileName,
            "The updater host log path is invalid.");

        var lines = new List<string>(MaximumLines)
        {
            $"{timestamp:O} {safeCode}",
        };
        if (UpdatePathSecurity.PathEntryExists(path))
        {
            UpdatePathSecurity.EnsureRegularFile(path, "The updater host log path is invalid.");
            var info = new FileInfo(path);
            if (info.Length is > 0 and <= MaximumLogBytes)
            {
                var prior = File.ReadLines(path, Encoding.UTF8)
                    .Where(static line => line.Length is > 0 and <= MaximumLineCharacters)
                    .TakeLast(MaximumLines - 1)
                    .ToArray();
                lines.InsertRange(0, prior);
            }
        }

        TrimToBounds(lines);
        var temporaryPath = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        UpdatePathSecurity.EnsureDirectChild(
            temporaryPath,
            directory,
            Path.GetFileName(temporaryPath),
            "The updater host temporary log path is invalid.");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(
                       stream,
                       new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                       4096,
                       leaveOpen: true))
            {
                foreach (var line in lines)
                {
                    writer.WriteLine(line);
                }

                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            UpdatePathSecurity.EnsureRegularFile(
                temporaryPath,
                "The updater host temporary log path is invalid.");
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static void TrimToBounds(List<string> lines)
    {
        while (lines.Count > MaximumLines)
        {
            lines.RemoveAt(0);
        }

        while (GetSerializedByteCount(lines) > MaximumLogBytes && lines.Count > 1)
        {
            lines.RemoveAt(0);
        }

        if (GetSerializedByteCount(lines) > MaximumLogBytes)
        {
            throw new InvalidDataException("The updater host failure record exceeds its size limit.");
        }
    }

    private static int GetSerializedByteCount(IEnumerable<string> lines) =>
        Encoding.UTF8.GetByteCount(string.Join(Environment.NewLine, lines) + Environment.NewLine);

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (!UpdatePathSecurity.PathEntryExists(path))
            {
                return;
            }

            UpdatePathSecurity.EnsureRegularFile(
                path,
                "The updater host temporary log path is invalid.");
            File.Delete(path);
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException)
        {
        }
    }
}
