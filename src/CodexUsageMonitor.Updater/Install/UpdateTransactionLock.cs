namespace CodexUsageMonitor.Updater.Install;

public sealed class UpdateTransactionLock : IDisposable
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private readonly FileStream _stream;
    private bool _disposed;

    private UpdateTransactionLock(FileStream stream)
    {
        _stream = stream;
    }

    public static UpdateTransactionLock Acquire(string installationDirectory, Guid transactionId)
    {
        var result = TryAcquire(installationDirectory, transactionId);
        return result ?? throw new IOException("The update transaction is already active.");
    }

    public static UpdateTransactionLock? TryAcquire(string installationDirectory, Guid transactionId) =>
        TryAcquirePath(
            UpdatePathLayout.GetTransactionLockPath(installationDirectory, transactionId),
            transactionId.ToString("D"));

    public static UpdateTransactionLock AcquireInventory(string installationDirectory)
    {
        var result = TryAcquireInventory(installationDirectory);
        return result ?? throw new IOException("The update transaction inventory is already active.");
    }

    public static UpdateTransactionLock? TryAcquireInventory(string installationDirectory) =>
        TryAcquirePath(
            UpdatePathLayout.GetTransactionInventoryLockPath(installationDirectory),
            "inventory");

    private static UpdateTransactionLock? TryAcquirePath(string path, string identityValue)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidDataException("The update transaction lock path is invalid.");
        Directory.CreateDirectory(directory);
        UpdatePathSecurity.EnsureDirectory(
            directory,
            "The update transaction lock directory is invalid.");
        if (UpdatePathSecurity.PathEntryExists(path))
        {
            UpdatePathSecurity.EnsureRegularFile(path, "The update transaction lock path is invalid.");
        }

        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                64,
                FileOptions.WriteThrough);
            stream.SetLength(0);
            var identity = System.Text.Encoding.ASCII.GetBytes(identityValue);
            stream.Write(identity);
            stream.Flush(flushToDisk: true);
            var result = new UpdateTransactionLock(stream);
            stream = null;
            return result;
        }
        catch (IOException exception) when (IsSharingOrLockViolation(exception))
        {
            return null;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
    }

    private static bool IsSharingOrLockViolation(IOException exception)
    {
        var nativeCode = exception.HResult & 0xFFFF;
        return nativeCode is ErrorSharingViolation or ErrorLockViolation;
    }
}
