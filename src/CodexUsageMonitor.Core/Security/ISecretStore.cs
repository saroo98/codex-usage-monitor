namespace CodexUsageMonitor.Core.Security;

public interface ISecretStore
{
    Task SetAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken);

    Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken);

    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

public interface IProtectedDataStore
{
    byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> purpose);

    byte[] Unprotect(ReadOnlySpan<byte> protectedData, ReadOnlySpan<byte> purpose);
}
