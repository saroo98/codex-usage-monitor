using System.Text.Json;
using CodexUsageMonitor.Updater.Install;
using CodexUsageMonitor.Updater.Staging;

namespace CodexUsageMonitor.IntegrationTests;

internal sealed class PortableUpdateTestFixture : IDisposable
{
    private static readonly string PublisherThumbprint = new('A', 40);

    private PortableUpdateTestFixture(
        string root,
        UpdateInstallRequest request,
        string installedApplication,
        string stagedApplication,
        string stagedHost,
        DateTimeOffset now)
    {
        Root = root;
        Request = request;
        InstalledApplication = installedApplication;
        StagedApplication = stagedApplication;
        StagedHost = stagedHost;
        Now = now;
    }

    public string Root { get; }
    public UpdateInstallRequest Request { get; }
    public string InstalledApplication { get; }
    public string StagedApplication { get; }
    public string StagedHost { get; }
    public DateTimeOffset Now { get; }

    public static async Task<PortableUpdateTestFixture> CreateAsync(bool portableDataMode = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "cum-portable-update", Guid.NewGuid().ToString("N"));
        var install = Path.Combine(root, "app");
        var stage = UpdatePathLayout.GetStagingDirectory(install, "1.0.1");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(stage);

        var installedApplication = Path.Combine(install, UpdatePathLayout.ApplicationExecutableName);
        var stagedApplication = Path.Combine(stage, UpdatePathLayout.ApplicationExecutableName);
        var stagedHost = Path.Combine(stage, UpdatePathLayout.UpdaterHostExecutableName);
        await File.WriteAllTextAsync(installedApplication, "current-application");
        await File.WriteAllTextAsync(stagedApplication, "target-application");
        await File.WriteAllTextAsync(stagedHost, "trusted-updater-host");
        await File.WriteAllTextAsync(Path.Combine(stage, "runtime-component.dll"), "runtime-component");

        if (portableDataMode)
        {
            await File.WriteAllBytesAsync(Path.Combine(install, "portable.mode"), []);
            var dataDirectory = Path.Combine(install, "data");
            Directory.CreateDirectory(dataDirectory);
            await File.WriteAllTextAsync(Path.Combine(dataDirectory, "settings.json"), "current-user-data");
        }

        var packageManifestHash = await WritePackageManifestAsync(stage, "1.0.1");
        var now = DateTimeOffset.UtcNow;
        var request = UpdateInstallRequest.Create(
            "1.0.1",
            "1.0.0",
            1234,
            now.AddSeconds(-5),
            install,
            stage,
            portableDataMode,
            await UpdateFileIntegrity.ComputeSha256Async(installedApplication, CancellationToken.None),
            await UpdateFileIntegrity.ComputeSha256Async(stagedApplication, CancellationToken.None),
            await UpdateFileIntegrity.ComputeSha256Async(stagedHost, CancellationToken.None),
            packageManifestHash,
            UpdateArtifactTrustMode.PublisherSignature,
            [PublisherThumbprint],
            now);
        Directory.CreateDirectory(Path.GetDirectoryName(request.UpdaterHostPath)!);
        File.Copy(stagedHost, request.UpdaterHostPath);
        return new PortableUpdateTestFixture(root, request, installedApplication, stagedApplication, stagedHost, now);
    }

    public async Task WritePreparedJournalAsync()
    {
        var journal = UpdateTransactionJournal.Create(Request, UpdateTransactionState.Prepared, Now);
        await journal.WriteAsync(JournalPath, CancellationToken.None);
    }

    public void InstallStagedPayload()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Request.BackupDirectory)!);
        Directory.Move(Request.InstallationDirectory, Request.BackupDirectory);
        Directory.Move(Request.StagingDirectory, Request.InstallationDirectory);
    }

    public async Task<UpdateTransactionJournal> AdvanceJournalAsync(
        params UpdateTransactionState[] states)
    {
        var journal = await ReadJournalAsync();
        var timestamp = journal.UpdatedAtUtc;
        foreach (var state in states)
        {
            timestamp = timestamp.AddMilliseconds(1);
            var safeCode = state is UpdateTransactionState.RollingBack or
                UpdateTransactionState.RolledBack or
                UpdateTransactionState.Failed
                ? "update.test_failure"
                : null;
            journal = journal.WithState(state, timestamp, safeCode);
        }

        await journal.WriteAsync(JournalPath, CancellationToken.None);
        return journal;
    }

    public async Task WriteHealthMarkerAsync(
        UpdateTransactionJournal journal,
        int processId = 7001,
        DateTimeOffset? processStartedAtUtc = null)
    {
        await StartupHealthMarker.WriteAsync(
            journal,
            processId,
            processStartedAtUtc ?? Now.AddSeconds(1),
            Now.AddSeconds(2),
            CancellationToken.None);
    }

    public Task<UpdateTransactionJournal> ReadJournalAsync() =>
        UpdateTransactionJournal.ReadAsync(JournalPath, CancellationToken.None);

    public string JournalPath => UpdatePathLayout.GetTransactionJournalPath(
        Request.InstallationDirectory,
        Request.TransactionId);

    public static async Task<string> WritePackageManifestAsync(string stage, string version)
    {
        var entries = new List<UpdatePackageFileEntry>();
        foreach (var path in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(stage, path).Replace(Path.DirectorySeparatorChar, '/');
            if (string.Equals(relative, UpdatePathLayout.PackageFileManifestName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(relative, "portable.mode", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(relative, "data", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("data/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var info = new FileInfo(path);
            entries.Add(new UpdatePackageFileEntry(
                relative,
                info.Length,
                await UpdateFileIntegrity.ComputeSha256Async(path, CancellationToken.None)));
        }

        entries.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));
        var manifest = new UpdatePackageFileManifest(
            UpdatePackageFileManifest.CurrentSchemaVersion,
            version,
            entries);
        var manifestPath = Path.Combine(stage, UpdatePathLayout.PackageFileManifestName);
        await using (var stream = new FileStream(
                         manifestPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         16 * 1024,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, manifest);
            await stream.FlushAsync();
        }

        return await UpdateFileIntegrity.ComputeSha256Async(manifestPath, CancellationToken.None);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
