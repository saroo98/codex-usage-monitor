using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace CodexUsageMonitor.Updater.Install;

public sealed record UpdateRollbackRequest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("transactionId")] Guid TransactionId,
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("parentProcessId")] int ParentProcessId,
    [property: JsonPropertyName("parentProcessStartedAtUtc")] DateTimeOffset ParentProcessStartedAtUtc,
    [property: JsonPropertyName("installationDirectory")] string InstallationDirectory,
    [property: JsonPropertyName("journalPath")] string JournalPath,
    [property: JsonPropertyName("createdAtUtc")] DateTimeOffset CreatedAtUtc)
{
    public const int CurrentSchemaVersion = 2;
    public const int MaximumSerializedBytes = 16 * 1024;
    private static readonly TimeSpan MaximumRequestAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(2);

    public static UpdateRollbackRequest Create(
        UpdateTransactionJournal journal,
        int parentProcessId,
        DateTimeOffset parentProcessStartedAtUtc,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(journal);
        journal.ValidateForInstallation(journal.InstallationDirectory);
        var request = new UpdateRollbackRequest(
            CurrentSchemaVersion,
            journal.TransactionId,
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
            parentProcessId,
            parentProcessStartedAtUtc.ToUniversalTime(),
            journal.InstallationDirectory,
            UpdatePathLayout.GetTransactionJournalPath(journal.InstallationDirectory, journal.TransactionId),
            createdAtUtc.ToUniversalTime());
        request.ValidateStructure(createdAtUtc.ToUniversalTime());
        request.ValidateAgainst(journal);
        return request;
    }

    public void ValidateEnvelope(
        string expectedNonce,
        string runningUpdaterHostPath,
        string requestFilePath,
        DateTimeOffset nowUtc)
    {
        if (!IsCanonicalNonce(Nonce) ||
            !IsCanonicalNonce(expectedNonce) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Nonce),
                Encoding.ASCII.GetBytes(expectedNonce)))
        {
            throw new InvalidDataException("Update rollback request identity is invalid.");
        }

        ValidateStructure(nowUtc.ToUniversalTime());
        var expectedHostPath = UpdatePathLayout.GetUpdaterHostPath(InstallationDirectory, TransactionId);
        UpdatePathSecurity.EnsureExactPath(
            runningUpdaterHostPath,
            expectedHostPath,
            "Update rollback host path is invalid.");
        UpdatePathSecurity.EnsureExactPath(
            requestFilePath,
            UpdatePathLayout.GetRollbackRequestPath(InstallationDirectory, TransactionId),
            "Update rollback request file path is invalid.");
        UpdatePathSecurity.EnsureHostInvocationRequestPath(
            runningUpdaterHostPath,
            requestFilePath,
            UpdatePathLayout.RollbackRequestFileName);
    }

    public void ValidateAgainst(UpdateTransactionJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        journal.ValidateForInstallation(journal.InstallationDirectory);
        if (journal.TransactionId != TransactionId ||
            journal.State is UpdateTransactionState.Prepared or
                UpdateTransactionState.WaitingForApplicationExit or
                UpdateTransactionState.Committed or
                UpdateTransactionState.RolledBack)
        {
            throw new InvalidDataException("Update rollback request does not match an eligible transaction journal.");
        }

        UpdatePathSecurity.EnsureExactPath(
            InstallationDirectory,
            journal.InstallationDirectory,
            "Update rollback installation path is invalid.");
        UpdatePathSecurity.EnsureExactPath(
            JournalPath,
            UpdatePathLayout.GetTransactionJournalPath(journal.InstallationDirectory, TransactionId),
            "Update rollback journal path is invalid.");
        UpdatePathSecurity.EnsureExactPath(
            journal.UpdaterHostPath,
            UpdatePathLayout.GetUpdaterHostPath(InstallationDirectory, TransactionId),
            "Update rollback host path is invalid.");
    }

    public static Task<UpdateRollbackRequest> ReadAsync(string path, CancellationToken cancellationToken) =>
        BoundedJsonFile.ReadAsync<UpdateRollbackRequest>(
            path,
            MaximumSerializedBytes,
            "The update rollback request is invalid or exceeds its size limit.",
            cancellationToken);

    public async Task WriteAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = UpdatePathLayout.NormalizePath(path);
        UpdatePathSecurity.EnsureExactPath(
            fullPath,
            UpdatePathLayout.GetRollbackRequestPath(InstallationDirectory, TransactionId),
            "Update rollback request file path is invalid.");
        ValidateStructure(DateTimeOffset.UtcNow);
        await BoundedJsonFile.WriteAsync(
            fullPath,
            this,
            MaximumSerializedBytes,
            overwrite: true,
            "The serialized update rollback request is invalid or exceeds its size limit.",
            cancellationToken).ConfigureAwait(false);
    }

    private void ValidateStructure(DateTimeOffset nowUtc)
    {
        if (SchemaVersion != CurrentSchemaVersion ||
            TransactionId == Guid.Empty ||
            ParentProcessId <= 0 ||
            ParentProcessStartedAtUtc == default ||
            ParentProcessStartedAtUtc.Offset != TimeSpan.Zero ||
            !IsCanonicalNonce(Nonce))
        {
            throw new InvalidDataException("Update rollback request identity is invalid.");
        }

        var now = nowUtc.ToUniversalTime();
        if (CreatedAtUtc == default ||
            CreatedAtUtc.Offset != TimeSpan.Zero ||
            CreatedAtUtc.Year is < 2000 or > 9998 ||
            CreatedAtUtc < now - MaximumRequestAge ||
            CreatedAtUtc > now + MaximumClockSkew ||
            ParentProcessStartedAtUtc > CreatedAtUtc + MaximumClockSkew)
        {
            throw new InvalidDataException("Update rollback request has expired.");
        }

        if (string.IsNullOrWhiteSpace(InstallationDirectory) ||
            string.IsNullOrWhiteSpace(JournalPath) ||
            InstallationDirectory.Length > UpdatePathLayout.MaximumPathCharacters ||
            JournalPath.Length > UpdatePathLayout.MaximumPathCharacters)
        {
            throw new InvalidDataException("Update rollback request path metadata is invalid.");
        }

        var installation = UpdatePathLayout.NormalizeInstallationDirectory(InstallationDirectory);
        UpdatePathSecurity.EnsureExactPath(
            InstallationDirectory,
            installation,
            "Update rollback installation path is not canonical.");
        UpdatePathSecurity.EnsureExactPath(
            JournalPath,
            UpdatePathLayout.GetTransactionJournalPath(installation, TransactionId),
            "Update rollback journal path is invalid.");
        UpdatePathSecurity.EnsureNoReparsePoints(installation);
        UpdatePathSecurity.EnsureNoReparsePoints(Path.GetDirectoryName(JournalPath)!);
        UpdatePathSecurity.EnsureNoReparsePoints(
            UpdatePathLayout.GetUpdaterHostDirectory(installation, TransactionId));
    }

    private static bool IsCanonicalNonce(string? value) =>
        value is { Length: 64 } &&
        value.All(char.IsAsciiHexDigit) &&
        string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal);
}
