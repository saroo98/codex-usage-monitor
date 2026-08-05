using System.IO.Compression;
using System.Security.Cryptography;
using CodexUsageMonitor.Migration.Discovery;
using CodexUsageMonitor.Migration.Execution;

namespace CodexUsageMonitor.MigrationTests;

[TestClass]
public sealed class LegacyBackupServiceTests
{
    [TestMethod]
    public async Task CreateAsyncCopiesHashesArchivesAndReopensEveryEntry()
    {
        using var fixture = new TemporaryDirectory();
        var data = Directory.CreateDirectory(Path.Combine(fixture.Path, "data")).FullName;
        var install = Directory.CreateDirectory(Path.Combine(fixture.Path, "install")).FullName;
        var migration = Directory.CreateDirectory(Path.Combine(fixture.Path, "migration")).FullName;
        var config = Path.Combine(data, "config.json");
        var state = Path.Combine(data, "state.json");
        var version = Path.Combine(install, "VERSION");
        await File.WriteAllTextAsync(config, "{\"poll_seconds\":60}");
        await File.WriteAllTextAsync(state, "{\"limits\":[]}");
        await File.WriteAllTextAsync(version, "5.0.0\n");
        var installation = new LegacyInstallation(
            data,
            install,
            "5.0.0",
            config,
            state,
            Path.Combine(data, "ui-state.json"),
            [config, state, version]);

        var result = await new LegacyBackupService().CreateAsync(installation, migration, CancellationToken.None);

        Assert.IsTrue(Directory.Exists(result.DirectoryPath));
        Assert.IsTrue(File.Exists(result.ArchivePath));
        Assert.IsGreaterThan(0L, result.ArchiveSizeBytes);
        await using (var archiveBytes = File.OpenRead(result.ArchivePath))
        {
            Assert.AreEqual(
                Convert.ToHexStringLower(await SHA256.HashDataAsync(archiveBytes)),
                result.ArchiveSha256);
        }

        using var archive = ZipFile.OpenRead(result.ArchivePath);
        CollectionAssert.AreEquivalent(
            new[] { "backup-manifest.json", "data/config.json", "data/state.json", "install/VERSION" },
            archive.Entries.Select(static entry => entry.FullName).ToArray());
        foreach (var entry in archive.Entries)
        {
            await using var stream = entry.Open();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            Assert.IsGreaterThan(0L, memory.Length);
        }
    }

    [TestMethod]
    public async Task CreateAsyncRejectsSourcesOutsideDiscoveredRootsAndCleansPartialOutput()
    {
        using var fixture = new TemporaryDirectory();
        var data = Directory.CreateDirectory(Path.Combine(fixture.Path, "data")).FullName;
        var install = Directory.CreateDirectory(Path.Combine(fixture.Path, "install")).FullName;
        var migration = Directory.CreateDirectory(Path.Combine(fixture.Path, "migration")).FullName;
        var external = Path.Combine(fixture.Path, "external.json");
        await File.WriteAllTextAsync(external, "{}");
        var installation = new LegacyInstallation(
            data,
            install,
            null,
            Path.Combine(data, "config.json"),
            Path.Combine(data, "state.json"),
            Path.Combine(data, "ui-state.json"),
            [external]);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new LegacyBackupService().CreateAsync(installation, migration, CancellationToken.None));

        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(migration));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"CodexUsageMonitorTests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
