namespace CodexUsageMonitor.Persistence.Files;

public static class AtomicFileWriter
{
    public static async Task WriteAsync(
        string destinationPath,
        Func<Stream, CancellationToken, Task> writer,
        CancellationToken cancellationToken,
        bool retainBackup = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(writer);
        var destination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                32 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await writer(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(destination))
            {
                var backup = destination + ".previous";
                File.Replace(temporary, destination, backup, ignoreMetadataErrors: true);
                if (!retainBackup)
                {
                    TryDelete(backup);
                }
            }
            else
            {
                File.Move(temporary, destination);
            }
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
