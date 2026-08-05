namespace CodexUsageMonitor.Updater.Install;

public interface IUpdaterHostFileCopier
{
    Task CopyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken);
}

public sealed class UpdaterHostFileCopier : IUpdaterHostFileCopier
{
    public async Task CopyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        UpdatePathSecurity.EnsureRegularFile(
            sourcePath,
            "The staged updater host is unavailable or unsafe.");
        if (UpdatePathSecurity.PathEntryExists(destinationPath))
        {
            throw new IOException("The updater host destination is already occupied.");
        }

        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
    }
}
