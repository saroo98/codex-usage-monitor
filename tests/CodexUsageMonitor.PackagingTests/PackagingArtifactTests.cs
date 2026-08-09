using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace CodexUsageMonitor.PackagingTests;

[TestClass]
public sealed class PackagingArtifactTests
{
    [TestMethod]
    public async Task DeterministicZipProducesByteIdenticalArchives()
    {
        using var fixture = new TemporaryDirectory();
        var source = Directory.CreateDirectory(Path.Combine(fixture.Path, "source")).FullName;
        await File.WriteAllTextAsync(Path.Combine(source, "b.txt"), "second");
        await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "first");
        var first = Path.Combine(fixture.Path, "first.zip");
        var second = Path.Combine(fixture.Path, "second.zip");

        await RunAsync("python", $"tools/deterministic_zip.py --source \"{source}\" --output \"{first}\"");
        File.SetLastWriteTimeUtc(Path.Combine(source, "a.txt"), DateTime.UtcNow.AddDays(1));
        await RunAsync("python", $"tools/deterministic_zip.py --source \"{source}\" --output \"{second}\"");

        CollectionAssert.AreEqual(await File.ReadAllBytesAsync(first), await File.ReadAllBytesAsync(second));
        await RunAsync("python", $"tools/deterministic_zip.py --verify \"{first}\"");
        await RunAsync("python", $"tools/deterministic_zip.py --compare \"{first}\" \"{second}\"");
    }

    [TestMethod]
    public void PortablePublishUsesIsolatedSdkArtifacts()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot(), "eng", "publish-portable.ps1"));
        StringAssert.Contains(script, "--artifacts-path");
        StringAssert.Contains(script, "$buildArtifactsRoot");
    }

    [TestMethod]
    public void PortableDistributionDeclaresLocalDataAndRemovalContract()
    {
        var root = RepositoryRoot();
        var packagingScript = File.ReadAllText(Path.Combine(root, "eng", "package-portable.ps1"));
        var verifierScript = File.ReadAllText(Path.Combine(root, "eng", "verify-release.ps1"));
        var portableReadme = File.ReadAllText(Path.Combine(root, "packaging", "portable", "README.md"));
        var install = File.ReadAllText(Path.Combine(root, "packaging", "portable", "INSTALL.txt"));
        var uninstall = File.ReadAllText(Path.Combine(root, "packaging", "portable", "UNINSTALL.txt"));

        StringAssert.Contains(packagingScript, "portable.mode");
        StringAssert.Contains(packagingScript, "INSTALL.txt");
        StringAssert.Contains(packagingScript, "UNINSTALL.txt");
        StringAssert.Contains(packagingScript, "Update payloads must not carry it");
        StringAssert.Contains(verifierScript, "Assert-PortableArchive");
        StringAssert.Contains(verifierScript, "CodexUsageMonitor/portable.mode");
        StringAssert.Contains(portableReadme, "data` directory");
        StringAssert.Contains(install, "portable.mode");
        StringAssert.Contains(uninstall, "Delete the extracted CodexUsageMonitor folder");
    }

    [TestMethod]
    public async Task UpdateArchiveVerifierAcceptsExactManifestAndRejectsUnsafePath()
    {
        using var fixture = new TemporaryDirectory();
        var valid = Path.Combine(fixture.Path, "valid.zip");
        CreateUpdateArchive(valid, unsafePath: false);

        await RunAsync("python", $"tools/verify_update_archive.py --archive \"{valid}\" --version 6.0.0");

        var unsafeArchive = Path.Combine(fixture.Path, "unsafe.zip");
        CreateUpdateArchive(unsafeArchive, unsafePath: true);
        var result = await RunAsync("python", $"tools/verify_update_archive.py --archive \"{unsafeArchive}\" --version 6.0.0", expectSuccess: false);
        Assert.AreNotEqual(0, result.ExitCode);
        StringAssert.Contains(result.Error, "unsafe");
    }

    [TestMethod]
    public async Task ReleaseVerifierFailsWhenArtifactMatrixIsIncomplete()
    {
        using var fixture = new TemporaryDirectory();

        var result = await RunAsync(
            "pwsh",
            $"-NoProfile -File eng/verify-release.ps1 -ReleaseRoot \"{fixture.Path}\" -Version 6.0.0 -Architectures x64",
            expectSuccess: false);

        Assert.AreNotEqual(0, result.ExitCode);
        StringAssert.Contains(result.Error + result.Output, "missing or empty");
    }

    [TestMethod]
    public void ProductAndPackagingTemplatesUseCentralVersionContract()
    {
        var root = RepositoryRoot();
        var props = XDocument.Load(Path.Combine(root, "Directory.Build.props"));
        var version = props.Descendants("VersionPrefix").Single().Value;
        Assert.AreEqual("6.0.0", version);

        var orchestrator = File.ReadAllText(Path.Combine(root, "eng", "package-release.ps1"));
        StringAssert.Contains(orchestrator, "Get-ProductVersion");
        StringAssert.Contains(orchestrator, "does not match product version");
        StringAssert.Contains(orchestrator, "Production packaging requires a clean working tree");
        var buildProperties = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        StringAssert.Contains(buildProperties, "ReleasePackagingRestore");
        StringAssert.Contains(buildProperties, "NuGetLockFilePath");
        var manifest = File.ReadAllText(Path.Combine(root, "packaging", "templates", "msix", "AppxManifest.xml"));
        StringAssert.Contains(manifest, "@@PACKAGE_VERSION@@");
    }

    [TestMethod]
    public void ProductionPackagingDeclaresAllFailClosedInputs()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot(), "eng", "package-release.ps1"));
        foreach (var input in new[]
        {
            "SigningCertificatePath", "TimestampUrl", "UpdatePrivateKeyPath", "UpdateTrustAnchor",
            "FeedBaseUri", "ReleaseNotesUri", "PublisherThumbprints",
        })
        {
            StringAssert.Contains(script, input);
        }
        StringAssert.Contains(script, "Production packaging is missing required values");
    }

    [TestMethod]
    public async Task UpdateManifestSignerMatchesConfiguredTrustAnchor()
    {
        using var fixture = new TemporaryDirectory();
        var privateKey = Path.Combine(fixture.Path, "ed25519.key");
        await File.WriteAllBytesAsync(privateKey, Convert.FromHexString(
            "9D61B19DEFFD5A60BA844AF492EC2CC4" +
            "4449C5697B326919703BAC031CAE7F60"));
        var publicKey = Convert.ToBase64String(Convert.FromHexString(
            "D75A980182B10AB7D54BFED3C964073A" +
            "0EE172F3DAA62325AF021A68F707511A"));
        var manifestPath = Path.Combine(fixture.Path, "update-manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            channel = "stable",
            version = "6.0.0",
            publishedAtUtc = new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero),
            minimumOsBuild = 19041,
            releaseNotesUrl = "https://example.invalid/releases/6.0.0",
            assets = Array.Empty<object>(),
            signature = string.Empty,
        }));
        var tool = "tools/CodexUsageMonitor.ReleaseTool/CodexUsageMonitor.ReleaseTool.csproj";

        await RunAsync(
            "dotnet",
            $"run --project {tool} --configuration Debug -- sign --manifest \"{manifestPath}\" --private-key \"{privateKey}\" --trust-anchor {publicKey}");
        await RunAsync(
            "dotnet",
            $"run --project {tool} --configuration Debug -- verify --manifest \"{manifestPath}\" --trust-anchor {publicKey}");

        using var signedManifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        Assert.IsFalse(string.IsNullOrWhiteSpace(signedManifest.RootElement.GetProperty("signature").GetString()));
    }

    private static void CreateUpdateArchive(string path, bool unsafePath)
    {
        var files = new Dictionary<string, byte[]>
        {
            ["CodexUsageMonitor.exe"] = [1, 2, 3],
            ["CodexUsageMonitor.UpdaterHost.exe"] = [4, 5, 6],
        };
        var manifestFiles = files.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new
        {
            path = pair.Key,
            sizeBytes = pair.Value.Length,
            sha256 = Convert.ToHexStringLower(SHA256.HashData(pair.Value)),
        }).ToArray();
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            version = "6.0.0",
            files = manifestFiles,
        });

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var pair in files)
        {
            var entry = archive.CreateEntry(pair.Key);
            using var stream = entry.Open();
            stream.Write(pair.Value);
        }
        var manifestEntry = archive.CreateEntry("update-files.json");
        using (var stream = manifestEntry.Open()) { stream.Write(manifest); }
        if (unsafePath)
        {
            var entry = archive.CreateEntry("../escape.txt");
            using var stream = entry.Open();
            stream.WriteByte(0);
        }
    }

    private static async Task<ProcessResult> RunAsync(string fileName, string arguments, bool expectSuccess = true)
    {
        var start = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = RepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(start) ?? throw new AssertFailedException($"Could not start {fileName}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        var result = new ProcessResult(process.ExitCode, await output, await error);
        if (expectSuccess && result.ExitCode != 0)
        {
            Assert.Fail($"{fileName} failed with {result.ExitCode}: {result.Error}\n{result.Output}");
        }
        return result;
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Directory.Build.props")))
        {
            current = current.Parent;
        }
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"CodexUsageMonitorPackaging-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
