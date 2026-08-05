using System.Text.Json.Serialization;
using CodexUsageMonitor.Updater.Model;
using CodexUsageMonitor.Updater.Staging;

namespace CodexUsageMonitor.Updater.Install;

public enum UpdateTransactionState
{
    Prepared,
    WaitingForApplicationExit,
    BackedUp,
    Installed,
    Validating,
    Committed,
    RollingBack,
    RolledBack,
    Failed,
}

public sealed record UpdateTransactionJournal(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("transactionId")] Guid TransactionId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("expectedCurrentVersion")] string ExpectedCurrentVersion,
    [property: JsonPropertyName("state")] UpdateTransactionState State,
    [property: JsonPropertyName("updatedAtUtc")] DateTimeOffset UpdatedAtUtc,
    [property: JsonPropertyName("safeErrorCode")] string? SafeErrorCode,
    [property: JsonPropertyName("installationDirectory")] string InstallationDirectory,
    [property: JsonPropertyName("stagingDirectory")] string StagingDirectory,
    [property: JsonPropertyName("backupDirectory")] string BackupDirectory,
    [property: JsonPropertyName("healthMarkerPath")] string HealthMarkerPath,
    [property: JsonPropertyName("updaterHostPath")] string UpdaterHostPath,
    [property: JsonPropertyName("applicationExecutableName")] string ApplicationExecutableName,
    [property: JsonPropertyName("portableDataMode")] bool PortableDataMode,
    [property: JsonPropertyName("expectedCurrentApplicationSha256")] string ExpectedCurrentApplicationSha256,
    [property: JsonPropertyName("targetApplicationSha256")] string TargetApplicationSha256,
    [property: JsonPropertyName("updaterHostSha256")] string UpdaterHostSha256,
    [property: JsonPropertyName("packageFileManifestSha256")] string PackageFileManifestSha256,
    [property: JsonPropertyName("trustMode")] UpdateArtifactTrustMode TrustMode,
    [property: JsonPropertyName("publisherThumbprints")] IReadOnlyList<string> PublisherThumbprints)
{
    public const int CurrentSchemaVersion = 2;
    public const int MaximumSerializedBytes = 64 * 1024;

    public static UpdateTransactionJournal Create(
        UpdateInstallRequest request,
        UpdateTransactionState state,
        DateTimeOffset updatedAtUtc,
        string? safeErrorCode = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (state is not (UpdateTransactionState.Prepared or UpdateTransactionState.Failed))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        var journal = new UpdateTransactionJournal(
            CurrentSchemaVersion,
            request.TransactionId,
            request.Version,
            request.ExpectedCurrentVersion,
            state,
            updatedAtUtc.ToUniversalTime(),
            NormalizeSafeErrorCode(safeErrorCode),
            request.InstallationDirectory,
            request.StagingDirectory,
            request.BackupDirectory,
            request.HealthMarkerPath,
            request.UpdaterHostPath,
            request.ApplicationExecutableName,
            request.PortableDataMode,
            request.ExpectedCurrentApplicationSha256,
            request.TargetApplicationSha256,
            request.UpdaterHostSha256,
            request.PackageFileManifestSha256,
            request.TrustMode,
            request.PublisherThumbprints.ToArray());
        journal.ValidateForInstallation(request.InstallationDirectory);
        return journal;
    }

    public UpdateTransactionJournal WithState(
        UpdateTransactionState state,
        DateTimeOffset updatedAtUtc,
        string? safeErrorCode = null)
    {
        if (!IsAllowedTransition(State, state))
        {
            throw new InvalidOperationException($"Update transaction transition {State} to {state} is invalid.");
        }

        var timestamp = updatedAtUtc.ToUniversalTime();
        if (timestamp <= UpdatedAtUtc)
        {
            if (UpdatedAtUtc.Year >= 9998)
            {
                throw new InvalidOperationException("The update transaction timestamp range is exhausted.");
            }

            timestamp = UpdatedAtUtc.AddTicks(1);
        }

        var next = this with
        {
            State = state,
            UpdatedAtUtc = timestamp,
            SafeErrorCode = NormalizeSafeErrorCode(safeErrorCode),
        };
        next.ValidateForInstallation(InstallationDirectory);
        return next;
    }

    public void ValidateForInstallation(string installationDirectory)
    {
        if (SchemaVersion != CurrentSchemaVersion || TransactionId == Guid.Empty || !Enum.IsDefined(State))
        {
            throw new InvalidDataException("Update transaction identity is invalid.");
        }

        if (!SemanticVersion.TryParse(Version, out var targetVersion) ||
            !SemanticVersion.TryParse(ExpectedCurrentVersion, out var currentVersion) ||
            !string.Equals(targetVersion.ToString(), Version, StringComparison.Ordinal) ||
            !string.Equals(currentVersion.ToString(), ExpectedCurrentVersion, StringComparison.Ordinal) ||
            targetVersion <= currentVersion)
        {
            throw new InvalidDataException("Update transaction version metadata is invalid.");
        }

        if (UpdatedAtUtc == default || UpdatedAtUtc.Offset != TimeSpan.Zero || UpdatedAtUtc.Year is < 2000 or > 9998)
        {
            throw new InvalidDataException("Update transaction timestamp is invalid.");
        }

        ValidateSafeErrorCodeForState(State, SafeErrorCode);
        if (!IsCanonicalSha256(ExpectedCurrentApplicationSha256) ||
            !IsCanonicalSha256(TargetApplicationSha256) ||
            !IsCanonicalSha256(UpdaterHostSha256) ||
            !IsCanonicalSha256(PackageFileManifestSha256) ||
            !Enum.IsDefined(TrustMode))
        {
            throw new InvalidDataException("Update transaction integrity metadata is invalid.");
        }

        UpdatePublisherPins.ValidateCanonical(
            PublisherThumbprints,
            allowEmpty: TrustMode is UpdateArtifactTrustMode.DevelopmentFileManifest);

        EnsureRequiredPath(InstallationDirectory);
        EnsureRequiredPath(StagingDirectory);
        EnsureRequiredPath(BackupDirectory);
        EnsureRequiredPath(HealthMarkerPath);
        EnsureRequiredPath(UpdaterHostPath);
        if (string.IsNullOrWhiteSpace(ApplicationExecutableName))
        {
            throw new InvalidDataException("Update transaction executable name is invalid.");
        }

        var install = UpdatePathLayout.NormalizeInstallationDirectory(installationDirectory);
        UpdatePathSecurity.EnsureExactPath(InstallationDirectory, install, "Update transaction belongs to a different installation.");
        UpdatePathSecurity.EnsureExactPath(StagingDirectory, UpdatePathLayout.GetStagingDirectory(install, Version), "Update transaction staging path is invalid.");
        UpdatePathSecurity.EnsureExactPath(BackupDirectory, UpdatePathLayout.GetBackupDirectory(install, TransactionId), "Update transaction backup path is invalid.");
        UpdatePathSecurity.EnsureExactPath(HealthMarkerPath, UpdatePathLayout.GetHealthMarkerPath(install, TransactionId), "Update transaction health path is invalid.");
        UpdatePathSecurity.EnsureExactPath(UpdaterHostPath, UpdatePathLayout.GetUpdaterHostPath(install, TransactionId), "Update transaction host path is invalid.");
        if (!string.Equals(ApplicationExecutableName, UpdatePathLayout.ApplicationExecutableName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Update transaction executable name is invalid.");
        }

        UpdatePathSecurity.EnsureNoReparsePoints(install);
        UpdatePathSecurity.EnsureNoReparsePoints(StagingDirectory);
        UpdatePathSecurity.EnsureNoReparsePoints(Path.GetDirectoryName(BackupDirectory)!);
        UpdatePathSecurity.EnsureNoReparsePoints(Path.GetDirectoryName(HealthMarkerPath)!);
        UpdatePathSecurity.EnsureNoReparsePoints(Path.GetDirectoryName(UpdaterHostPath)!);
    }

    public static Task<UpdateTransactionJournal> ReadAsync(string path, CancellationToken cancellationToken) =>
        BoundedJsonFile.ReadAsync<UpdateTransactionJournal>(
            path,
            MaximumSerializedBytes,
            "The update transaction journal is invalid or exceeds its size limit.",
            cancellationToken);

    public async Task WriteAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = UpdatePathLayout.NormalizePath(path);
        UpdatePathSecurity.EnsureExactPath(
            fullPath,
            UpdatePathLayout.GetTransactionJournalPath(InstallationDirectory, TransactionId),
            "Update transaction journal path is invalid.");
        ValidateForInstallation(InstallationDirectory);
        await BoundedJsonFile.WriteAsync(
            fullPath,
            this,
            MaximumSerializedBytes,
            overwrite: true,
            "The serialized update transaction journal is invalid or exceeds its size limit.",
            cancellationToken).ConfigureAwait(false);
    }

    private static bool IsAllowedTransition(UpdateTransactionState current, UpdateTransactionState next) =>
        current switch
        {
            UpdateTransactionState.Prepared => next is UpdateTransactionState.WaitingForApplicationExit or UpdateTransactionState.Failed,
            UpdateTransactionState.WaitingForApplicationExit => next is UpdateTransactionState.BackedUp or UpdateTransactionState.Failed,
            UpdateTransactionState.BackedUp => next is UpdateTransactionState.Installed or UpdateTransactionState.Committed or UpdateTransactionState.RollingBack or UpdateTransactionState.RolledBack or UpdateTransactionState.Failed,
            UpdateTransactionState.Installed => next is UpdateTransactionState.Validating or UpdateTransactionState.Committed or UpdateTransactionState.RollingBack or UpdateTransactionState.RolledBack or UpdateTransactionState.Failed,
            UpdateTransactionState.Validating => next is UpdateTransactionState.Committed or UpdateTransactionState.RollingBack or UpdateTransactionState.RolledBack or UpdateTransactionState.Failed,
            UpdateTransactionState.RollingBack => next is UpdateTransactionState.RollingBack or UpdateTransactionState.RolledBack or UpdateTransactionState.Failed,
            UpdateTransactionState.Failed => next is UpdateTransactionState.Failed or UpdateTransactionState.Committed or UpdateTransactionState.RollingBack or UpdateTransactionState.RolledBack,
            UpdateTransactionState.Committed => next is UpdateTransactionState.Committed,
            UpdateTransactionState.RolledBack => next is UpdateTransactionState.RolledBack,
            _ => false,
        };

    private static void ValidateSafeErrorCodeForState(UpdateTransactionState state, string? value)
    {
        if (value is { Length: > 128 } ||
            value?.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')) == true)
        {
            throw new InvalidDataException("Update transaction error metadata is invalid.");
        }

        if (state is UpdateTransactionState.Failed or UpdateTransactionState.RollingBack or UpdateTransactionState.RolledBack)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException("A failed or rollback update transaction requires a safe error code.");
            }
        }
        else if (value is not null)
        {
            throw new InvalidDataException("A successful update transaction cannot contain an error code.");
        }
    }

    private static string? NormalizeSafeErrorCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length > 128 || normalized.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
        {
            throw new ArgumentException("Safe error code is invalid.", nameof(value));
        }

        return normalized;
    }

    private static void EnsureRequiredPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > UpdatePathLayout.MaximumPathCharacters)
        {
            throw new InvalidDataException("Update transaction path metadata is invalid.");
        }
    }

    private static bool IsCanonicalSha256(string? value) =>
        UpdateFileIntegrity.IsSha256(value) &&
        string.Equals(value, value!.ToLowerInvariant(), StringComparison.Ordinal);
}
