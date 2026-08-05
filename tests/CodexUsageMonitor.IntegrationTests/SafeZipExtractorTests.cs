using System.IO.Compression;
using CodexUsageMonitor.Updater.Staging;

namespace CodexUsageMonitor.IntegrationTests;

[TestClass]
public sealed class SafeZipExtractorTests
{
    [TestMethod]
    public async Task ExtractsCanonicalRegularFiles()
    {
        using var fixture = new ZipFixture();
        fixture.AddFile("app/CodexUsageMonitor.exe", "application");
        fixture.AddFile("app/runtime.dll", "runtime");

        await new SafeZipExtractor().ExtractAsync(
            fixture.SealAndGetArchivePath(),
            fixture.TargetDirectory,
            CancellationToken.None);

        Assert.AreEqual(
            "application",
            await File.ReadAllTextAsync(Path.Combine(
                fixture.TargetDirectory,
                "app",
                "CodexUsageMonitor.exe")));
    }

    [TestMethod]
    [DataRow("../escape.txt")]
    [DataRow("/absolute.txt")]
    [DataRow("C:/drive.txt")]
    [DataRow("app/../escape.txt")]
    [DataRow("app/CON.txt")]
    [DataRow("app/trailing. ")]
    public async Task RejectsUnsafeWindowsPaths(string entryName)
    {
        using var fixture = new ZipFixture();
        fixture.AddFile(entryName, "unsafe");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            new SafeZipExtractor().ExtractAsync(
                fixture.SealAndGetArchivePath(),
                fixture.TargetDirectory,
                CancellationToken.None));

        Assert.IsFalse(Directory.Exists(fixture.TargetDirectory));
    }

    [TestMethod]
    public async Task RejectsCaseAliasedDuplicateEntries()
    {
        using var fixture = new ZipFixture();
        fixture.AddFile("app/runtime.dll", "first");
        fixture.AddFile("APP/RUNTIME.DLL", "second");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            new SafeZipExtractor().ExtractAsync(
                fixture.SealAndGetArchivePath(),
                fixture.TargetDirectory,
                CancellationToken.None));
    }

    [TestMethod]
    public async Task RejectsFileDirectoryCollisionRegardlessOfArchiveOrder()
    {
        using var fixture = new ZipFixture();
        fixture.AddFile("app/child.dll", "child");
        fixture.AddFile("app", "file-collision");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            new SafeZipExtractor().ExtractAsync(
                fixture.SealAndGetArchivePath(),
                fixture.TargetDirectory,
                CancellationToken.None));
    }

    [TestMethod]
    public async Task RejectsUnixSymbolicLinkEntry()
    {
        using var fixture = new ZipFixture();
        var entry = fixture.AddFile("app/link", "target");
        entry.ExternalAttributes = unchecked((int)0xA1FF0000);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            new SafeZipExtractor().ExtractAsync(
                fixture.SealAndGetArchivePath(),
                fixture.TargetDirectory,
                CancellationToken.None));
    }

    private sealed class ZipFixture : IDisposable
    {
        private readonly string _root;
        private readonly FileStream _stream;
        private readonly ZipArchive _archive;
        private bool _sealed;

        public ZipFixture()
        {
            _root = Path.Combine(Path.GetTempPath(), "cum-safe-zip", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            ArchivePath = Path.Combine(_root, "update.zip");
            TargetDirectory = Path.Combine(_root, "extracted");
            _stream = new FileStream(ArchivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            _archive = new ZipArchive(_stream, ZipArchiveMode.Create, leaveOpen: true);
        }

        public string ArchivePath { get; }
        public string TargetDirectory { get; }

        public ZipArchiveEntry AddFile(string name, string content)
        {
            if (_sealed)
            {
                throw new InvalidOperationException("The archive is already sealed.");
            }

            var entry = _archive.CreateEntry(name, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
            return entry;
        }

        private void Seal()
        {
            if (_sealed)
            {
                return;
            }

            _archive.Dispose();
            _stream.Dispose();
            _sealed = true;
        }

        public void Dispose()
        {
            Seal();
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        public string SealAndGetArchivePath()
        {
            Seal();
            return ArchivePath;
        }
    }
}
