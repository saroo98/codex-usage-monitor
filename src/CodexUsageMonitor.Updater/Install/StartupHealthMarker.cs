using System.Text.Json.Serialization;

namespace CodexUsageMonitor.Updater.Install;

public sealed record StartupHealthMarkerDocument(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("transactionId")] Guid TransactionId,
    [property: JsonPropertyName("targetApplicationSha256")] string TargetApplicationSha256,
    [property: JsonPropertyName("processId")] int ProcessId,
    [property: JsonPropertyName("processStartedAtUtc")] DateTimeOffset ProcessStartedAtUtc,
    [property: JsonPropertyName("writtenAtUtc")] DateTimeOffset WrittenAtUtc)
{
    public const int CurrentSchemaVersion = 1;
}

public static class StartupHealthMarker
{
    public const int MaximumSerializedBytes = 4 * 1024;
    private static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(2);

    public static async Task<bool> IsValidAsync(
        UpdateTransactionJournal journal,
        int? expectedProcessId,
        DateTimeOffset? expectedProcessStartedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        journal.ValidateForInstallation(journal.InstallationDirectory);
        ValidateExpectedProcessIdentity(expectedProcessId, expectedProcessStartedAtUtc);

        var markerPath = GetValidatedMarkerPath(journal);
        if (!UpdatePathSecurity.PathEntryExists(markerPath))
        {
            return false;
        }

        try
        {
            var marker = await BoundedJsonFile.ReadAsync<StartupHealthMarkerDocument>(
                markerPath,
                MaximumSerializedBytes,
                "The startup health marker is invalid.",
                cancellationToken).ConfigureAwait(false);
            if (!IsValidDocument(marker, journal))
            {
                return false;
            }

            if (expectedProcessId is { } processId && marker.ProcessId != processId)
            {
                return false;
            }

            return expectedProcessStartedAtUtc is not { } startedAtUtc ||
                marker.ProcessStartedAtUtc == startedAtUtc.ToUniversalTime();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            OverflowException)
        {
            return false;
        }
    }

    public static async Task WriteAsync(
        UpdateTransactionJournal journal,
        int processId,
        DateTimeOffset processStartedAtUtc,
        DateTimeOffset writtenAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        journal.ValidateForInstallation(journal.InstallationDirectory);
        if (journal.State is not (UpdateTransactionState.BackedUp or
            UpdateTransactionState.Installed or
            UpdateTransactionState.Validating))
        {
            throw new InvalidDataException("The update transaction is not eligible for a startup health marker.");
        }

        var processStarted = processStartedAtUtc.ToUniversalTime();
        var written = writtenAtUtc.ToUniversalTime();
        ValidateProcessIdentity(processId, processStarted, written);
        var marker = new StartupHealthMarkerDocument(
            StartupHealthMarkerDocument.CurrentSchemaVersion,
            journal.TransactionId,
            journal.TargetApplicationSha256,
            processId,
            processStarted,
            written);
        if (!IsValidDocument(marker, journal))
        {
            throw new InvalidDataException("The startup health marker metadata is invalid.");
        }

        var markerPath = GetValidatedMarkerPath(journal);
        var directory = Path.GetDirectoryName(markerPath)
            ?? throw new InvalidDataException("The startup health marker path is invalid.");
        Directory.CreateDirectory(directory);
        UpdatePathSecurity.EnsureDirectory(
            directory,
            "The startup health marker directory is invalid.");
        if (UpdatePathSecurity.PathEntryExists(markerPath))
        {
            UpdatePathSecurity.EnsureRegularFile(
                markerPath,
                "The startup health marker path is occupied by an invalid filesystem entry.");
        }

        await BoundedJsonFile.WriteAsync(
            markerPath,
            marker,
            MaximumSerializedBytes,
            overwrite: true,
            "The startup health marker is invalid or exceeds its size limit.",
            cancellationToken).ConfigureAwait(false);
    }

    private static string GetValidatedMarkerPath(UpdateTransactionJournal journal)
    {
        var markerPath = UpdatePathLayout.NormalizePath(journal.HealthMarkerPath);
        UpdatePathSecurity.EnsureExactPath(
            markerPath,
            UpdatePathLayout.GetHealthMarkerPath(journal.InstallationDirectory, journal.TransactionId),
            "The startup health marker path does not match the update transaction.");
        return markerPath;
    }

    private static bool IsValidDocument(
        StartupHealthMarkerDocument marker,
        UpdateTransactionJournal journal)
    {
        if (marker.SchemaVersion != StartupHealthMarkerDocument.CurrentSchemaVersion ||
            marker.TransactionId != journal.TransactionId ||
            marker.ProcessId <= 0 ||
            marker.ProcessStartedAtUtc == default ||
            marker.ProcessStartedAtUtc.Offset != TimeSpan.Zero ||
            marker.ProcessStartedAtUtc.Year is < 2000 or > 9998 ||
            marker.WrittenAtUtc == default ||
            marker.WrittenAtUtc.Offset != TimeSpan.Zero ||
            marker.WrittenAtUtc.Year is < 2000 or > 9998 ||
            marker.ProcessStartedAtUtc > marker.WrittenAtUtc + MaximumClockSkew ||
            !UpdateFileIntegrity.IsSha256(marker.TargetApplicationSha256) ||
            !string.Equals(
                marker.TargetApplicationSha256,
                marker.TargetApplicationSha256.ToLowerInvariant(),
                StringComparison.Ordinal))
        {
            return false;
        }

        return UpdateFileIntegrity.FixedTimeEquals(
            journal.TargetApplicationSha256,
            marker.TargetApplicationSha256);
    }

    private static void ValidateExpectedProcessIdentity(
        int? expectedProcessId,
        DateTimeOffset? expectedProcessStartedAtUtc)
    {
        if (expectedProcessId is <= 0 ||
            (expectedProcessId.HasValue != expectedProcessStartedAtUtc.HasValue))
        {
            throw new ArgumentException("The expected startup process identity is invalid.");
        }

        if (expectedProcessStartedAtUtc is { } startedAtUtc &&
            (startedAtUtc == default || startedAtUtc.Offset != TimeSpan.Zero))
        {
            throw new ArgumentException("The expected startup process identity is invalid.");
        }
    }

    private static void ValidateProcessIdentity(
        int processId,
        DateTimeOffset processStartedAtUtc,
        DateTimeOffset writtenAtUtc)
    {
        if (processId <= 0 ||
            processStartedAtUtc == default ||
            processStartedAtUtc.Offset != TimeSpan.Zero ||
            processStartedAtUtc.Year is < 2000 or > 9998 ||
            writtenAtUtc == default ||
            writtenAtUtc.Offset != TimeSpan.Zero ||
            writtenAtUtc.Year is < 2000 or > 9998 ||
            processStartedAtUtc > writtenAtUtc + MaximumClockSkew)
        {
            throw new ArgumentException("The startup process identity is invalid.");
        }
    }
}
