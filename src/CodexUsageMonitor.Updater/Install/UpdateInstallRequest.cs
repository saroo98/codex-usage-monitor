using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using CodexUsageMonitor.Updater.Model;
using CodexUsageMonitor.Updater.Staging;

namespace CodexUsageMonitor.Updater.Install;

public sealed record UpdateInstallRequest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("transactionId")] Guid TransactionId,
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("expectedCurrentVersion")] string ExpectedCurrentVersion,
    [property: JsonPropertyName("parentProcessId")] int ParentProcessId,
    [property: JsonPropertyName("parentProcessStartedAtUtc")] DateTimeOffset ParentProcessStartedAtUtc,
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
    [property: JsonPropertyName("publisherThumbprints")] IReadOnlyList<string> PublisherThumbprints,
    [property: JsonPropertyName("createdAtUtc")] DateTimeOffset CreatedAtUtc)
{
    public const int CurrentSchemaVersion = 2;
    public const int MaximumSerializedBytes = 64 * 1024;
    private static readonly TimeSpan MaximumRequestAge = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(2);

    public static UpdateInstallRequest Create(
        string version,
        string expectedCurrentVersion,
        int parentProcessId,
        DateTimeOffset parentProcessStartedAtUtc,
        string installationDirectory,
        string stagingDirectory,
        bool portableDataMode,
        string expectedCurrentApplicationSha256,
        string targetApplicationSha256,
        string updaterHostSha256,
        string packageFileManifestSha256,
        UpdateArtifactTrustMode trustMode,
        IEnumerable<string> publisherThumbprints,
        DateTimeOffset createdAtUtc)
    {
        var transactionId = Guid.NewGuid();
        var install = UpdatePathLayout.NormalizeInstallationDirectory(installationDirectory);
        var stage = UpdatePathLayout.NormalizePath(stagingDirectory);
        var allowEmptyPins = trustMode is UpdateArtifactTrustMode.ProjectManifest or
            UpdateArtifactTrustMode.DevelopmentFileManifest;
        var normalizedPublisherThumbprints = UpdatePublisherPins.Normalize(publisherThumbprints, allowEmptyPins);
        var request = new UpdateInstallRequest(
            CurrentSchemaVersion,
            transactionId,
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
            version,
            expectedCurrentVersion,
            parentProcessId,
            parentProcessStartedAtUtc.ToUniversalTime(),
            install,
            stage,
            UpdatePathLayout.GetBackupDirectory(install, transactionId),
            UpdatePathLayout.GetHealthMarkerPath(install, transactionId),
            UpdatePathLayout.GetUpdaterHostPath(install, transactionId),
            UpdatePathLayout.ApplicationExecutableName,
            portableDataMode,
            NormalizeSha256(expectedCurrentApplicationSha256),
            NormalizeSha256(targetApplicationSha256),
            NormalizeSha256(updaterHostSha256),
            NormalizeSha256(packageFileManifestSha256),
            trustMode,
            normalizedPublisherThumbprints,
            createdAtUtc.ToUniversalTime());
        request.ValidateStructure(createdAtUtc.ToUniversalTime(), requireExistingPayload: false);
        return request;
    }

    public void Validate(
        string expectedNonce,
        string runningUpdaterHostPath,
        string requestFilePath,
        DateTimeOffset nowUtc)
    {
        if (!IsCanonicalNonce(expectedNonce) || !IsCanonicalNonce(Nonce))
        {
            throw new InvalidDataException("Update request identity is invalid.");
        }

        var nonce = Encoding.ASCII.GetBytes(Nonce);
        var expected = Encoding.ASCII.GetBytes(expectedNonce);
        if (!CryptographicOperations.FixedTimeEquals(nonce, expected))
        {
            throw new InvalidDataException("Update request identity is invalid.");
        }

        ValidateStructure(nowUtc.ToUniversalTime(), requireExistingPayload: true);
        UpdatePathSecurity.EnsureExactPath(
            runningUpdaterHostPath,
            UpdaterHostPath,
            "The updater host path does not match the prepared transaction.");
        UpdatePathSecurity.EnsureExactPath(
            requestFilePath,
            UpdatePathLayout.GetInstallRequestPath(InstallationDirectory, TransactionId),
            "The updater request file path is invalid.");
        UpdatePathSecurity.EnsureHostInvocationRequestPath(
            runningUpdaterHostPath,
            requestFilePath,
            UpdatePathLayout.InstallRequestFileName);
    }

    public async Task VerifyStagedPayloadAsync(CancellationToken cancellationToken)
    {
        await UpdateFileIntegrity.VerifySha256Async(
            Path.Combine(InstallationDirectory, ApplicationExecutableName),
            ExpectedCurrentApplicationSha256,
            "The installed application changed after the update was prepared.",
            cancellationToken).ConfigureAwait(false);

        var packageManifest = await UpdatePackageFileManifest.ReadAndVerifyAsync(
            StagingDirectory,
            Version,
            cancellationToken).ConfigureAwait(false);
        if (!UpdateFileIntegrity.FixedTimeEquals(PackageFileManifestSha256, packageManifest.ManifestSha256))
        {
            throw new CryptographicException("The staged package integrity manifest changed after verification.");
        }

        await UpdateFileIntegrity.VerifySha256Async(
            Path.Combine(StagingDirectory, ApplicationExecutableName),
            TargetApplicationSha256,
            "The staged application changed after trust verification.",
            cancellationToken).ConfigureAwait(false);
        await UpdateFileIntegrity.VerifySha256Async(
            Path.Combine(StagingDirectory, UpdatePathLayout.UpdaterHostExecutableName),
            UpdaterHostSha256,
            "The staged updater host changed after trust verification.",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task VerifyPayloadAsync(CancellationToken cancellationToken)
    {
        await VerifyStagedPayloadAsync(cancellationToken).ConfigureAwait(false);
        await UpdateFileIntegrity.VerifySha256Async(
            UpdaterHostPath,
            UpdaterHostSha256,
            "The external updater host copy failed integrity verification.",
            cancellationToken).ConfigureAwait(false);
    }

    public static Task<UpdateInstallRequest> ReadAsync(string path, CancellationToken cancellationToken) =>
        BoundedJsonFile.ReadAsync<UpdateInstallRequest>(
            path,
            MaximumSerializedBytes,
            "The update request is invalid or exceeds its size limit.",
            cancellationToken);

    public async Task WriteAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = UpdatePathLayout.NormalizePath(path);
        UpdatePathSecurity.EnsureExactPath(
            fullPath,
            UpdatePathLayout.GetInstallRequestPath(InstallationDirectory, TransactionId),
            "The updater request file path is invalid.");
        ValidateStructure(DateTimeOffset.UtcNow, requireExistingPayload: true);
        await BoundedJsonFile.WriteAsync(
            fullPath,
            this,
            MaximumSerializedBytes,
            overwrite: false,
            "The serialized update request is invalid or exceeds its size limit.",
            cancellationToken).ConfigureAwait(false);
    }

    internal void ValidateAgainst(UpdateTransactionJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (journal.TransactionId != TransactionId ||
            !string.Equals(journal.Version, Version, StringComparison.Ordinal) ||
            !string.Equals(journal.ExpectedCurrentVersion, ExpectedCurrentVersion, StringComparison.Ordinal) ||
            journal.PortableDataMode != PortableDataMode ||
            !string.Equals(journal.ExpectedCurrentApplicationSha256, ExpectedCurrentApplicationSha256, StringComparison.Ordinal) ||
            !string.Equals(journal.TargetApplicationSha256, TargetApplicationSha256, StringComparison.Ordinal) ||
            !string.Equals(journal.UpdaterHostSha256, UpdaterHostSha256, StringComparison.Ordinal) ||
            !string.Equals(journal.PackageFileManifestSha256, PackageFileManifestSha256, StringComparison.Ordinal) ||
            journal.TrustMode != TrustMode ||
            !journal.PublisherThumbprints.SequenceEqual(PublisherThumbprints, StringComparer.Ordinal))
        {
            throw new InvalidDataException("The update request does not match its transaction journal.");
        }

        UpdatePathSecurity.EnsureExactPath(journal.InstallationDirectory, InstallationDirectory, "The transaction installation path changed.");
        UpdatePathSecurity.EnsureExactPath(journal.StagingDirectory, StagingDirectory, "The transaction staging path changed.");
        UpdatePathSecurity.EnsureExactPath(journal.BackupDirectory, BackupDirectory, "The transaction backup path changed.");
        UpdatePathSecurity.EnsureExactPath(journal.HealthMarkerPath, HealthMarkerPath, "The transaction health path changed.");
        UpdatePathSecurity.EnsureExactPath(journal.UpdaterHostPath, UpdaterHostPath, "The transaction updater host path changed.");
    }

    internal void ValidateStructure(DateTimeOffset nowUtc, bool requireExistingPayload)
    {
        if (SchemaVersion != CurrentSchemaVersion || TransactionId == Guid.Empty || !IsCanonicalNonce(Nonce))
        {
            throw new InvalidDataException("Update request identity is invalid.");
        }

        var now = nowUtc.ToUniversalTime();
        if (CreatedAtUtc == default || CreatedAtUtc.Offset != TimeSpan.Zero ||
            CreatedAtUtc < now - MaximumRequestAge || CreatedAtUtc > now + MaximumClockSkew)
        {
            throw new InvalidDataException("Update request has expired.");
        }

        if (ParentProcessId <= 0 || ParentProcessStartedAtUtc == default ||
            ParentProcessStartedAtUtc.Offset != TimeSpan.Zero ||
            ParentProcessStartedAtUtc > CreatedAtUtc + MaximumClockSkew)
        {
            throw new InvalidDataException("Update parent process identity is invalid.");
        }

        if (!SemanticVersion.TryParse(Version, out var targetVersion) ||
            !SemanticVersion.TryParse(ExpectedCurrentVersion, out var currentVersion) ||
            !string.Equals(targetVersion.ToString(), Version, StringComparison.Ordinal) ||
            !string.Equals(currentVersion.ToString(), ExpectedCurrentVersion, StringComparison.Ordinal) ||
            targetVersion <= currentVersion)
        {
            throw new InvalidDataException("Update version metadata is invalid.");
        }

        if (!IsCanonicalSha256(ExpectedCurrentApplicationSha256) ||
            !IsCanonicalSha256(TargetApplicationSha256) ||
            !IsCanonicalSha256(UpdaterHostSha256) ||
            !IsCanonicalSha256(PackageFileManifestSha256) ||
            !Enum.IsDefined(TrustMode))
        {
            throw new InvalidDataException("Update file integrity metadata is invalid.");
        }

        var allowEmptyPins = TrustMode is UpdateArtifactTrustMode.ProjectManifest or
            UpdateArtifactTrustMode.DevelopmentFileManifest;
        UpdatePublisherPins.ValidateCanonical(PublisherThumbprints, allowEmptyPins);

        EnsureRequiredString(InstallationDirectory);
        EnsureRequiredString(StagingDirectory);
        EnsureRequiredString(BackupDirectory);
        EnsureRequiredString(HealthMarkerPath);
        EnsureRequiredString(UpdaterHostPath);
        EnsureRequiredString(ApplicationExecutableName);

        var install = UpdatePathLayout.NormalizeInstallationDirectory(InstallationDirectory);
        var expectedStage = UpdatePathLayout.GetStagingDirectory(install, Version);
        UpdatePathSecurity.EnsureExactPath(InstallationDirectory, install, "The update installation path is not canonical.");
        UpdatePathSecurity.EnsureExactPath(StagingDirectory, expectedStage, "Update staging escaped its versioned transaction root.");
        UpdatePathSecurity.EnsureExactPath(BackupDirectory, UpdatePathLayout.GetBackupDirectory(install, TransactionId), "Update backup path is invalid.");
        UpdatePathSecurity.EnsureExactPath(HealthMarkerPath, UpdatePathLayout.GetHealthMarkerPath(install, TransactionId), "Update health marker path is invalid.");
        UpdatePathSecurity.EnsureExactPath(UpdaterHostPath, UpdatePathLayout.GetUpdaterHostPath(install, TransactionId), "Update host path is invalid.");
        if (!string.Equals(ApplicationExecutableName, UpdatePathLayout.ApplicationExecutableName, StringComparison.Ordinal) ||
            !string.Equals(Path.GetFileName(ApplicationExecutableName), ApplicationExecutableName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Update application path is invalid.");
        }

        UpdatePathSecurity.EnsureNoReparsePoints(install);
        UpdatePathSecurity.EnsureNoReparsePoints(StagingDirectory);
        UpdatePathSecurity.EnsureNoReparsePoints(Path.GetDirectoryName(BackupDirectory)!);
        UpdatePathSecurity.EnsureNoReparsePoints(Path.GetDirectoryName(HealthMarkerPath)!);
        UpdatePathSecurity.EnsureNoReparsePoints(Path.GetDirectoryName(UpdaterHostPath)!);

        var installationExists = UpdatePathSecurity.PathEntryExists(install);
        if (installationExists)
        {
            UpdatePathSecurity.EnsureDirectory(
                install,
                "The update installation directory is invalid.");
            var portableMarker = Path.Combine(install, PortableUpdateData.MarkerFileName);
            var portableMarkerExists = UpdatePathSecurity.PathEntryExists(portableMarker);
            if (portableMarkerExists)
            {
                UpdatePathSecurity.EnsureRegularFile(
                    portableMarker,
                    "The portable-mode marker is invalid.");
            }

            if (portableMarkerExists != PortableDataMode)
            {
                throw new InvalidDataException("Portable data mode changed after the update request was created.");
            }
        }

        if (!requireExistingPayload)
        {
            return;
        }

        if (!installationExists)
        {
            throw new InvalidDataException("Update request directories are incomplete.");
        }

        UpdatePathSecurity.EnsureDirectory(
            StagingDirectory,
            "Update request directories are incomplete.");
        UpdatePathSecurity.EnsureRegularFile(
            Path.Combine(install, ApplicationExecutableName),
            "Update request directories are incomplete.");
        UpdatePathSecurity.EnsureRegularFile(
            Path.Combine(StagingDirectory, ApplicationExecutableName),
            "Update request directories are incomplete.");
        UpdatePathSecurity.EnsureRegularFile(
            Path.Combine(StagingDirectory, UpdatePathLayout.UpdaterHostExecutableName),
            "Update request directories are incomplete.");
        UpdatePathSecurity.EnsureRegularFile(
            Path.Combine(StagingDirectory, UpdatePathLayout.PackageFileManifestName),
            "Update request directories are incomplete.");
        UpdatePathSecurity.EnsureRegularFile(
            UpdaterHostPath,
            "Update request directories are incomplete.");
    }

    private static void EnsureRequiredString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > UpdatePathLayout.MaximumPathCharacters)
        {
            throw new InvalidDataException("Update request path metadata is invalid.");
        }
    }

    private static string NormalizeSha256(string value)
    {
        if (!UpdateFileIntegrity.IsSha256(value))
        {
            throw new InvalidDataException("Update file integrity metadata is invalid.");
        }

        return value.ToLowerInvariant();
    }

    private static bool IsCanonicalSha256(string? value) =>
        UpdateFileIntegrity.IsSha256(value) &&
        string.Equals(value, value!.ToLowerInvariant(), StringComparison.Ordinal);

    private static bool IsCanonicalNonce(string? value) =>
        value is { Length: 64 } &&
        value.All(char.IsAsciiHexDigit) &&
        string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal);
}
