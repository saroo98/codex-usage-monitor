using CodexUsageMonitor.Updater.Install;
using CodexUsageMonitor.Updater.Staging;

namespace CodexUsageMonitor.UnitTests;

[TestClass]
public sealed class UpdatePathLayoutTests
{
    private static readonly string CurrentHash = new('A', UpdateFileIntegrity.Sha256HexLength);
    private static readonly string TargetHash = new('B', UpdateFileIntegrity.Sha256HexLength);
    private static readonly string HostHash = new('C', UpdateFileIntegrity.Sha256HexLength);
    private static readonly string ManifestHash = new('E', UpdateFileIntegrity.Sha256HexLength);
    private static readonly string PublisherPin = new('D', 40);

    [TestMethod]
    public void WorkingRootIsSiblingOfInstallationEvenWithTrailingSeparator()
    {
        var parent = Path.Combine(Path.GetTempPath(), "cum-path-layout", Guid.NewGuid().ToString("N"));
        var install = Path.Combine(parent, "CodexUsageMonitor") + Path.DirectorySeparatorChar;

        var normalized = UpdatePathLayout.NormalizeInstallationDirectory(install);
        var updateRoot = UpdatePathLayout.GetUpdateRoot(install);

        Assert.AreEqual(Path.TrimEndingDirectorySeparator(Path.GetFullPath(install)), normalized);
        Assert.AreEqual(Path.Combine(parent, UpdatePathLayout.WorkingDirectoryName), updateRoot);
        Assert.IsFalse(updateRoot.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void CreateRequestKeepsStageBackupHealthAndHostOutsideInstallation()
    {
        var parent = Path.Combine(Path.GetTempPath(), "cum-request-layout", Guid.NewGuid().ToString("N"));
        var install = Path.Combine(parent, "app");
        var stage = Path.Combine(UpdatePathLayout.GetStagingRoot(install), "1.0.1");
        var now = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

        var request = UpdateInstallRequest.Create(
            "1.0.1",
            "1.0.0",
            42,
            now.AddSeconds(-1),
            install,
            stage,
            false,
            CurrentHash,
            TargetHash,
            HostHash,
            ManifestHash,
            UpdateArtifactTrustMode.PublisherSignature,
            [PublisherPin],
            now);

        var installPrefix = UpdatePathLayout.NormalizeInstallationDirectory(install) + Path.DirectorySeparatorChar;
        Assert.IsFalse(request.StagingDirectory.StartsWith(installPrefix, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(request.BackupDirectory.StartsWith(installPrefix, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(request.HealthMarkerPath.StartsWith(installPrefix, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(request.UpdaterHostPath.StartsWith(installPrefix, StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(
            UpdatePathLayout.GetTransactionJournalPath(install, request.TransactionId),
            Path.Combine(UpdatePathLayout.GetTransactionRoot(install), request.TransactionId.ToString("N") + ".json"));
    }

    [TestMethod]
    public void NormalizeInstallationRejectsVolumeRoot()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()));
        Assert.IsNotNull(root);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            UpdatePathLayout.NormalizeInstallationDirectory(root));
    }
}
