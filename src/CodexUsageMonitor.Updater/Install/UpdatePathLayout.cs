using System.Security.Cryptography;
using System.Text;
using CodexUsageMonitor.Updater.Model;

namespace CodexUsageMonitor.Updater.Install;

/// <summary>
/// Centralizes the portable updater's directory layout. Staging and backup data stay on
/// the installation volume so directory moves remain atomic. Requests, journals, health
/// markers, and copied updater hosts are stored under the current user's local application
/// data directory so another local account cannot replace the transaction control files.
/// </summary>
public static class UpdatePathLayout
{
    public const string WorkingDirectoryName = ".codex-usage-monitor-update";
    public const string ApplicationExecutableName = "CodexUsageMonitor.exe";
    public const string UpdaterHostExecutableName = "CodexUsageMonitor.UpdaterHost.exe";
    public const string InstallRequestFileName = "install-request.json";
    public const string RollbackRequestFileName = "rollback-request.json";
    public const string PackageFileManifestName = "update-files.json";
    public const int MaximumPathCharacters = 1024;

    private const string ProductDirectoryName = "CodexUsageMonitor";
    private const string PrivateUpdateDirectoryName = "UpdateTransactions";

    public static string NormalizeInstallationDirectory(string installationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationDirectory);
        var normalized = NormalizePath(installationDirectory);
        if (!Path.IsPathFullyQualified(normalized) ||
            Path.GetPathRoot(normalized)?.Equals(normalized, PathComparison) == true)
        {
            throw new InvalidDataException("The installation directory is invalid.");
        }

        if (OperatingSystem.IsWindows() &&
            (normalized.StartsWith(@"\\", StringComparison.Ordinal) ||
             normalized.StartsWith(@"\\?\", StringComparison.Ordinal) ||
             normalized.StartsWith(@"\\.\", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("Portable updates require a local installation directory.");
        }

        return normalized;
    }

    public static string GetUpdateRoot(string installationDirectory)
    {
        var install = NormalizeInstallationDirectory(installationDirectory);
        var parent = Path.GetDirectoryName(install)
            ?? throw new InvalidDataException("The installation directory has no parent.");
        return Combine(parent, WorkingDirectoryName);
    }

    public static string GetPrivateRoot(string installationDirectory)
    {
        var install = NormalizeInstallationDirectory(installationDirectory);
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("The current user's local application data directory is unavailable.");
        }

        var userRoot = NormalizePath(localApplicationData);
        var keySource = OperatingSystem.IsWindows() ? install.ToUpperInvariant() : install;
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(keySource)))[..32];
        var privateRoot = Combine(userRoot, ProductDirectoryName, PrivateUpdateDirectoryName, key);
        UpdatePathSecurity.EnsureDescendant(
            privateRoot,
            userRoot,
            "The private update directory escaped local application data.");
        return privateRoot;
    }

    public static string GetDownloadRoot(string installationDirectory) =>
        Combine(GetUpdateRoot(installationDirectory), "downloads");

    public static string GetStagingRoot(string installationDirectory) =>
        Combine(GetUpdateRoot(installationDirectory), "staging");

    public static string GetStagingDirectory(string installationDirectory, string version)
    {
        if (!SemanticVersion.TryParse(version, out var parsed) ||
            !string.Equals(parsed.ToString(), version, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The staged update version is invalid.");
        }

        return Combine(GetStagingRoot(installationDirectory), version);
    }

    public static string GetBackupRoot(string installationDirectory) =>
        Combine(GetUpdateRoot(installationDirectory), "backup");

    public static string GetHealthRoot(string installationDirectory) =>
        Combine(GetPrivateRoot(installationDirectory), "health");

    public static string GetTransactionRoot(string installationDirectory) =>
        Combine(GetPrivateRoot(installationDirectory), "transactions");

    public static string GetUpdaterHostRoot(string installationDirectory) =>
        Combine(GetPrivateRoot(installationDirectory), "host");

    public static string GetBackupDirectory(string installationDirectory, Guid transactionId) =>
        Combine(GetBackupRoot(installationDirectory), ValidateTransactionId(transactionId).ToString("N"));

    public static string GetHealthMarkerPath(string installationDirectory, Guid transactionId) =>
        Combine(GetHealthRoot(installationDirectory), ValidateTransactionId(transactionId).ToString("N") + ".ready");

    public static string GetTransactionJournalPath(string installationDirectory, Guid transactionId) =>
        Combine(GetTransactionRoot(installationDirectory), ValidateTransactionId(transactionId).ToString("N") + ".json");

    public static string GetTransactionLockPath(string installationDirectory, Guid transactionId) =>
        Combine(GetTransactionRoot(installationDirectory), ValidateTransactionId(transactionId).ToString("N") + ".lock");

    public static string GetTransactionInventoryLockPath(string installationDirectory) =>
        Combine(GetTransactionRoot(installationDirectory), "inventory.lock");

    public static string GetUpdaterHostDirectory(string installationDirectory, Guid transactionId) =>
        Combine(GetUpdaterHostRoot(installationDirectory), ValidateTransactionId(transactionId).ToString("N"));

    public static string GetUpdaterHostPath(string installationDirectory, Guid transactionId) =>
        Combine(GetUpdaterHostDirectory(installationDirectory, transactionId), UpdaterHostExecutableName);

    public static string GetInstallRequestPath(string installationDirectory, Guid transactionId) =>
        Combine(GetUpdaterHostDirectory(installationDirectory, transactionId), InstallRequestFileName);

    public static string GetRollbackRequestPath(string installationDirectory, Guid transactionId) =>
        Combine(GetUpdaterHostDirectory(installationDirectory, transactionId), RollbackRequestFileName);

    public static string GetFailedInstallationDirectory(string installationDirectory, Guid transactionId) =>
        NormalizePath(NormalizeInstallationDirectory(installationDirectory) + $".failed-{ValidateTransactionId(transactionId):N}");

    public static string GetRollbackDataCheckpointDirectory(string installationDirectory, Guid transactionId) =>
        Combine(GetBackupRoot(installationDirectory), ValidateTransactionId(transactionId).ToString("N") + ".previous-data");

    public static string GetRollbackFailureDirectory(string installationDirectory, Guid transactionId) =>
        Combine(GetBackupRoot(installationDirectory), ValidateTransactionId(transactionId).ToString("N") + ".rollback-failure");

    public static bool TryParseTransactionId(string journalPath, out Guid transactionId)
    {
        transactionId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(journalPath) || journalPath.Length > MaximumPathCharacters)
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetExtension(journalPath), ".json", StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParseExact(Path.GetFileNameWithoutExtension(journalPath), "N", out transactionId) &&
                transactionId != Guid.Empty;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            transactionId = Guid.Empty;
            return false;
        }
    }

    internal static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Length > MaximumPathCharacters)
        {
            throw new InvalidDataException("The update path is too long.");
        }

        try
        {
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (normalized.Length is <= 0 or > MaximumPathCharacters)
            {
                throw new InvalidDataException("The update path is invalid or too long.");
            }

            return normalized;
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            throw new InvalidDataException("The update path is invalid or too long.", exception);
        }
    }

    private static string Combine(params string[] parts)
    {
        var combined = Path.Combine(parts);
        if (combined.Length > MaximumPathCharacters)
        {
            throw new InvalidDataException("The update path is too long.");
        }

        return combined;
    }

    private static Guid ValidateTransactionId(Guid transactionId) =>
        transactionId != Guid.Empty
            ? transactionId
            : throw new ArgumentOutOfRangeException(nameof(transactionId));
}
