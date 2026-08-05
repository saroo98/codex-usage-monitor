using System.Text.Json;
using CodexUsageMonitor.Updater.Install;
using CodexUsageMonitor.Updater.Staging;

namespace CodexUsageMonitor.UnitTests;

[TestClass]
public sealed class UpdatePackageFileManifestTests
{
    [TestMethod]
    public async Task ExactInventoryAndHashesAreAccepted()
    {
        using var fixture = await PackageFixture.CreateAsync();

        var verified = await UpdatePackageFileManifest.ReadAndVerifyAsync(
            fixture.Root,
            fixture.Version,
            CancellationToken.None);

        Assert.AreEqual(fixture.Version, verified.Manifest.Version);
        Assert.IsTrue(UpdateFileIntegrity.IsSha256(verified.ManifestSha256));
        Assert.AreEqual(3, verified.Manifest.Files.Count);
    }

    [TestMethod]
    public async Task UnlistedAndTamperedFilesAreRejected()
    {
        using var fixture = await PackageFixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "unexpected.dll"), "unexpected");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UpdatePackageFileManifest.ReadAndVerifyAsync(
                fixture.Root,
                fixture.Version,
                CancellationToken.None));

        File.Delete(Path.Combine(fixture.Root, "unexpected.dll"));
        await File.AppendAllTextAsync(fixture.ApplicationPath, "tampered");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UpdatePackageFileManifest.ReadAndVerifyAsync(
                fixture.Root,
                fixture.Version,
                CancellationToken.None));
    }

    [TestMethod]
    public async Task TraversalAndCaseAliasingEntriesAreRejected()
    {
        using var fixture = await PackageFixture.CreateAsync();
        var validEntries = fixture.Manifest.Files.ToArray();
        var traversal = fixture.Manifest with
        {
            Files =
            [
                new UpdatePackageFileEntry("../outside.bin", 1, new string('a', 64)),
                .. validEntries,
            ],
        };
        await fixture.WriteManifestAsync(traversal);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UpdatePackageFileManifest.ReadAndVerifyAsync(
                fixture.Root,
                fixture.Version,
                CancellationToken.None));

        var application = validEntries.Single(entry =>
            string.Equals(entry.Path, UpdatePathLayout.ApplicationExecutableName, StringComparison.Ordinal));
        var alias = application with { Path = application.Path.ToUpperInvariant() };
        var aliased = fixture.Manifest with
        {
            Files = [application, alias, .. validEntries.Where(entry => entry != application)],
        };
        await fixture.WriteManifestAsync(aliased);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UpdatePackageFileManifest.ReadAndVerifyAsync(
                fixture.Root,
                fixture.Version,
                CancellationToken.None));
    }

    private sealed class PackageFixture : IDisposable
    {
        private PackageFixture(
            string root,
            string version,
            string applicationPath,
            UpdatePackageFileManifest manifest)
        {
            Root = root;
            Version = version;
            ApplicationPath = applicationPath;
            Manifest = manifest;
        }

        public string Root { get; }
        public string Version { get; }
        public string ApplicationPath { get; }
        public UpdatePackageFileManifest Manifest { get; }

        public static async Task<PackageFixture> CreateAsync()
        {
            const string version = "1.0.1";
            var root = Path.Combine(Path.GetTempPath(), "cum-package-manifest", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var applicationPath = Path.Combine(root, UpdatePathLayout.ApplicationExecutableName);
            var hostPath = Path.Combine(root, UpdatePathLayout.UpdaterHostExecutableName);
            var libraryPath = Path.Combine(root, "runtime-component.dll");
            await File.WriteAllTextAsync(applicationPath, "application");
            await File.WriteAllTextAsync(hostPath, "updater");
            await File.WriteAllTextAsync(libraryPath, "library");

            var entries = new List<UpdatePackageFileEntry>();
            foreach (var path in new[] { applicationPath, hostPath, libraryPath })
            {
                var info = new FileInfo(path);
                entries.Add(new UpdatePackageFileEntry(
                    Path.GetFileName(path),
                    info.Length,
                    await UpdateFileIntegrity.ComputeSha256Async(path, CancellationToken.None)));
            }

            entries.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));
            var manifest = new UpdatePackageFileManifest(
                UpdatePackageFileManifest.CurrentSchemaVersion,
                version,
                entries);
            var fixture = new PackageFixture(root, version, applicationPath, manifest);
            await fixture.WriteManifestAsync(manifest);
            return fixture;
        }

        public async Task WriteManifestAsync(UpdatePackageFileManifest manifest)
        {
            var path = Path.Combine(Root, UpdatePathLayout.PackageFileManifestName);
            await using var stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await JsonSerializer.SerializeAsync(stream, manifest);
            await stream.FlushAsync();
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
}
