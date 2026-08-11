using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace CodexUsageMonitor.PackagingTests;

[TestClass]
public sealed class PackagingArtifactTests
{
    private const string Rfc8032TrustAnchor = "PUAXw+hDiVqStwqnTRt+vJyYLM8uxJaMwM1V8Sr0Zgw=";

    [TestMethod]
    public async Task PublicationAuditAcceptsOnlyReviewedSyntheticUrlUserInfoFixtures()
    {
        using var fixture = await CreatePublicationAuditFixtureAsync();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Path, "ReviewedModule.psm1"),
            "# Synthetic credential URI rejection fixtures.\n" +
            "$values = @('https://user@github.com/project', 'https://user:password@github.com/project', " +
            "'https://user@github.com/saroo98/codex-usage-monitor/releases/tag/v6.0.0', " +
            "'https://user:password@github.com')\n",
            new UTF8Encoding(false));

        var accepted = await RunAsync(
            "python",
            "eng/audit-publication.py",
            expectSuccess: false,
            workingDirectory: fixture.Path);
        Assert.AreEqual(0, accepted.ExitCode, accepted.Output + accepted.Error);
    }

    [TestMethod]
    public async Task PublicationAuditRejectsPersonalEmail()
    {
        using var fixture = await CreatePublicationAuditFixtureAsync();

        await File.WriteAllTextAsync(
            Path.Combine(fixture.Path, "contact.txt"),
            "Contact: maintainer" + "@" + "personal-domain.org\n",
            new UTF8Encoding(false));
        var rejected = await RunAsync(
            "python",
            "eng/audit-publication.py",
            expectSuccess: false,
            workingDirectory: fixture.Path);
        Assert.AreEqual(1, rejected.ExitCode, rejected.Output + rejected.Error);
        StringAssert.Contains(rejected.Output + rejected.Error, "personal or unapproved email address");
    }

    [TestMethod]
    [DataRow("personal-domain-userinfo")]
    [DataRow("personal-domain-password")]
    [DataRow("github-arbitrary-user")]
    [DataRow("github-arbitrary-user-and-path")]
    public async Task PublicationAuditRejectsUnreviewedUrlUserInfo(string mutation)
    {
        using var fixture = await CreatePublicationAuditFixtureAsync();
        var url = mutation switch
        {
            "personal-domain-userinfo" => "https://person" + "@" + "personal-domain.org/project",
            "personal-domain-password" => "https://person:secret" + "@" + "personal-domain.org/project",
            "github-arbitrary-user" => "https://real-user" + "@" + "github.com/project",
            "github-arbitrary-user-and-path" => "https://real-user:secret" + "@" + "github.com/private/project",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Path, "unreviewed-url.txt"),
            url + "\n",
            new UTF8Encoding(false));

        var rejected = await RunAsync(
            "python",
            "eng/audit-publication.py",
            expectSuccess: false,
            workingDirectory: fixture.Path);

        Assert.AreEqual(1, rejected.ExitCode, rejected.Output + rejected.Error);
        StringAssert.Contains(rejected.Output + rejected.Error, "personal or unapproved email address");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(127)]
    [DataRow(128)]
    public async Task PublicationAuditRejectsControlCharactersInUtf8PowerShellModules(int codePoint)
    {
        using var fixture = await CreatePublicationAuditFixtureAsync();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Path, "BinaryLookingModule.psm1"),
            "MZ" + char.ConvertFromUtf32(codePoint) + "payload\n",
            new UTF8Encoding(false));

        var rejected = await RunAsync(
            "python",
            "eng/audit-publication.py",
            expectSuccess: false,
            workingDirectory: fixture.Path);

        Assert.AreEqual(1, rejected.ExitCode, rejected.Output + rejected.Error);
        StringAssert.Contains(rejected.Output + rejected.Error, "disallowed control character");
    }

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
    public async Task PortablePublishPassesTheExplicitPublicUnsignedBuildFlavorToEveryDotnetPublish()
    {
        using var fixture = new TemporaryDirectory();
        var stubDirectory = Directory.CreateDirectory(Path.Combine(fixture.Path, "stub tools")).FullName;
        var invocationLog = Path.Combine(fixture.Path, "dotnet-invocations.txt");
        await File.WriteAllTextAsync(
            Path.Combine(stubDirectory, "dotnet.cmd"),
            "@echo off\r\n" +
            "setlocal EnableDelayedExpansion\r\n" +
            "echo %*>>\"%DOTNET_INVOCATION_LOG%\"\r\n" +
            "set prev=\r\n" +
            "set out=\r\n" +
            "for %%A in (%*) do (\r\n" +
            "  if \"!prev!\"==\"--output\" set out=%%~A\r\n" +
            "  set prev=%%~A\r\n" +
            ")\r\n" +
            "if not defined out exit /b 2\r\n" +
            "if not exist \"!out!\" mkdir \"!out!\"\r\n" +
            "echo application>\"!out!\\CodexUsageMonitor.exe\"\r\n" +
            "echo updater>\"!out!\\CodexUsageMonitor.UpdaterHost.exe\"\r\n" +
            "exit /b 0\r\n",
            new UTF8Encoding(false));
        var outputRoot = Path.Combine(fixture.Path, "publish output");
        var script = Path.Combine(RepositoryRoot(), "eng", "publish-portable.ps1");
        var command = "$env:PATH='" + stubDirectory + ";' + $env:PATH; " +
            "$env:DOTNET_INVOCATION_LOG='" + invocationLog + "'; " +
            $"& '{script}' -RuntimeIdentifier win-x64 -SelfContained $false -Configuration Debug -Version 6.0.0 -UpdateBuildFlavor PublicUnsigned -OutputRoot '{outputRoot}'";

        await RunAsync("pwsh", $"-NoProfile -Command \"{command}\"");

        var invocations = await File.ReadAllLinesAsync(invocationLog);
        Assert.AreEqual(2, invocations.Length, "Both application and updater publishes must run.");
        foreach (var invocation in invocations)
        {
            StringAssert.Contains(invocation, "-p:UpdateBuildFlavor=PublicUnsigned");
        }
    }

    [TestMethod]
    [DataRow("win-x64", "Debug")]
    [DataRow("win-arm64", "Release")]
    public async Task PortablePublishProducesByteIdenticalFirstPartyBinariesAcrossDistinctArtifactsRoots(
        string runtimeIdentifier,
        string configuration)
    {
        using var fixture = new TemporaryDirectory();
        var firstRoot = Path.Combine(fixture.Path, "first publish root");
        var secondRoot = Path.Combine(fixture.Path, "second publish root");
        var script = Path.Combine(RepositoryRoot(), "eng", "publish-portable.ps1");

        await RunAsync(
            "pwsh",
            $"-NoProfile -Command \"& '{script}' -RuntimeIdentifier {runtimeIdentifier} -SelfContained $false -Configuration {configuration} -Version 6.0.0 -OutputRoot '{firstRoot}'\"");
        await RunAsync(
            "pwsh",
            $"-NoProfile -Command \"& '{script}' -RuntimeIdentifier {runtimeIdentifier} -SelfContained $false -Configuration {configuration} -Version 6.0.0 -OutputRoot '{secondRoot}'\"");

        var firstHashes = HashFirstPartyPortableBinaries(Path.Combine(firstRoot, "portable"));
        var secondHashes = HashFirstPartyPortableBinaries(Path.Combine(secondRoot, "portable"));
        Assert.IsTrue(firstHashes.Count > 0, "The portable publish did not contain first-party binaries.");
        CollectionAssert.AreEqual(firstHashes.Keys.ToArray(), secondHashes.Keys.ToArray());
        foreach (var name in firstHashes.Keys)
        {
            Assert.AreEqual(firstHashes[name], secondHashes[name], $"First-party portable binary differs: {name}");
        }
    }

    [TestMethod]
    [DataRow("comma", ",")]
    [DataRow("semicolon", ";")]
    [DataRow("equals", "=")]
    [DataRow("percent-escape", "%3B")]
    public async Task PortablePublishRejectsUnsafePathMapCharactersBeforeCreatingOutputOrInvokingDotnet(
        string caseName,
        string unsafeCharacters)
    {
        using var fixture = new TemporaryDirectory();
        var stubDirectory = Directory.CreateDirectory(Path.Combine(fixture.Path, "stub tools")).FullName;
        var invocationMarker = Path.Combine(fixture.Path, "dotnet-invoked.txt");
        await File.WriteAllTextAsync(
            Path.Combine(stubDirectory, "dotnet.cmd"),
            "@echo off\r\ntype nul > \"%DOTNET_INVOCATION_MARKER%\"\r\nexit /b 0\r\n",
            new UTF8Encoding(false));
        var outputRoot = Path.Combine(fixture.Path, $"unsafe-{caseName}{unsafeCharacters}root");
        var script = Path.Combine(RepositoryRoot(), "eng", "publish-portable.ps1");
        var command = "$env:PATH='" + stubDirectory + ";' + $env:PATH; " +
            "$env:DOTNET_INVOCATION_MARKER='" + invocationMarker + "'; " +
            $"& '{script}' -RuntimeIdentifier win-x64 -SelfContained $false -Configuration Debug -Version 6.0.0 -OutputRoot '{outputRoot}'";

        var result = await RunAsync("pwsh", $"-NoProfile -Command \"{command}\"", expectSuccess: false);

        Assert.AreNotEqual(0, result.ExitCode, $"Unsafe PathMap case '{caseName}' was accepted.");
        Assert.IsFalse(File.Exists(invocationMarker), "dotnet was invoked before PathMap validation rejected the path.");
        Assert.IsFalse(Directory.Exists(outputRoot), "Output was created before PathMap validation rejected the path.");
        StringAssert.Contains(result.Error + result.Output, "cannot be represented safely in MSBuild PathMap");
        Assert.IsFalse((result.Error + result.Output).Contains(outputRoot, StringComparison.OrdinalIgnoreCase),
            "The rejection must not disclose the caller's output path.");
    }

    [TestMethod]
    public async Task PortablePackagingProducesByteIdenticalFlavorArchivesAcrossDistinctPackageRoots()
    {
        using var fixture = new TemporaryDirectory();
        var firstPublishRoot = Path.Combine(fixture.Path, "first publish tree");
        var secondPublishRoot = Path.Combine(fixture.Path, "second publish tree");
        foreach (var publishRoot in new[] { firstPublishRoot, secondPublishRoot })
        {
            foreach (var runtimeIdentifier in new[] { "win-x64", "win-arm64" })
            {
                foreach (var flavor in new[] { "self-contained", "framework-dependent" })
                {
                    var tree = Directory.CreateDirectory(Path.Combine(publishRoot, runtimeIdentifier, flavor)).FullName;
                    await File.WriteAllBytesAsync(Path.Combine(tree, "CodexUsageMonitor.exe"), [1, 2, 3]);
                    await File.WriteAllBytesAsync(Path.Combine(tree, "CodexUsageMonitor.UpdaterHost.exe"), [4, 5, 6]);
                    await File.WriteAllBytesAsync(Path.Combine(tree, "CodexUsageMonitor.Core.dll"), [7, 8, 9]);
                }
            }
        }

        var firstPackageRoot = Path.Combine(fixture.Path, "first package root");
        var secondPackageRoot = Path.Combine(fixture.Path, "second package root");
        var script = Path.Combine(RepositoryRoot(), "eng", "package-portable.ps1");
        await RunAsync(
            "pwsh",
            $"-NoProfile -Command \"& '{script}' -RuntimeIdentifiers @('win-x64','win-arm64') -Version 6.0.0 -PublishRoot '{firstPublishRoot}' -OutputRoot '{firstPackageRoot}'\"");
        await RunAsync(
            "pwsh",
            $"-NoProfile -Command \"& '{script}' -RuntimeIdentifiers @('win-x64','win-arm64') -Version 6.0.0 -PublishRoot '{secondPublishRoot}' -OutputRoot '{secondPackageRoot}'\"");

        var requiredPortableArchives = new[]
        {
            "CodexUsageMonitor-6.0.0-win-x64-portable-framework-dependent.zip",
            "CodexUsageMonitor-6.0.0-win-x64-portable-self-contained.zip",
            "CodexUsageMonitor-6.0.0-win-arm64-portable-framework-dependent.zip",
            "CodexUsageMonitor-6.0.0-win-arm64-portable-self-contained.zip",
        };
        foreach (var name in requiredPortableArchives)
        {
            CollectionAssert.AreEqual(
                await File.ReadAllBytesAsync(Path.Combine(firstPackageRoot, name)),
                await File.ReadAllBytesAsync(Path.Combine(secondPackageRoot, name)),
                $"Required portable archive differs: {name}");
        }
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
    public async Task ReleaseVerifierRejectsExtraArtifactOmittedFromChecksums()
    {
        using var fixture = new TemporaryDirectory();
        var names = new[]
        {
            "BUILD-METADATA.json", "THIRD-PARTY-NOTICES.md", "LICENSE.txt", "bom.json", "update-manifest.json",
            "CodexUsageMonitor-6.0.0-source.zip", "CodexUsageMonitor-6.0.0-win-x64-portable-framework-dependent.zip",
            "CodexUsageMonitor-6.0.0-win-x64-portable-self-contained.zip", "CodexUsageMonitor-6.0.0-win-x64-update.zip",
            "CodexUsageMonitor-6.0.0-x64.msix", "UNSIGNED-RELEASE-CANDIDATE.txt",
        };
        foreach (var name in names)
        {
            var content = name == "UNSIGNED-RELEASE-CANDIDATE.txt"
                ? "UNSIGNED VALIDATION ARTIFACTS\nThese files are not production-signed. Do not publish or distribute them as a release.\n"
                : "placeholder";
            await File.WriteAllTextAsync(Path.Combine(fixture.Path, name), content, new UTF8Encoding(false));
        }
        await File.WriteAllTextAsync(Path.Combine(fixture.Path, "unexpected.bin"), "omitted");
        var inventory = names.Select(name =>
            $"{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture.Path, name))))} *{name}");
        await File.WriteAllLinesAsync(Path.Combine(fixture.Path, "SHA256SUMS.txt"), inventory, new UTF8Encoding(false));

        var result = await RunAsync("pwsh",
            $"-NoProfile -File eng/verify-release.ps1 -ReleaseRoot \"{fixture.Path}\" -Version 6.0.0 -Architectures x64",
            expectSuccess: false);
        Assert.AreNotEqual(0, result.ExitCode);
        StringAssert.Contains(result.Error + result.Output, "unexpected.bin");
        StringAssert.Contains(result.Error + result.Output, "missing from checksum inventory");
    }

    [TestMethod]
    public async Task ReleaseArchiveExtractionRejectsEveryUnsafeArchiveShape()
    {
        using var fixture = new TemporaryDirectory();
        var module = Path.Combine(RepositoryRoot(), "eng", "ReleaseVerification.psm1");
        var validArchive = Path.Combine(fixture.Path, "valid.zip");
        CreateSecurityBoundaryArchive(validArchive, "valid");
        var validCommand = $"Import-Module '{module.Replace("'", "''", StringComparison.Ordinal)}' -Force; " +
            $"Test-ReleaseArchive -ArchivePath '{validArchive.Replace("'", "''", StringComparison.Ordinal)}' " +
            $"-TemporaryBase '{fixture.Path.Replace("'", "''", StringComparison.Ordinal)}' -ArchiveKind Update -Version 6.0.0";
        await RunAsync("pwsh", $"-NoProfile -Command \"{validCommand}\"");
        var cases = new[] { "traversal", "duplicate", "reparse", "data", "marker" };
        foreach (var mutation in cases)
        {
            var archivePath = Path.Combine(fixture.Path, mutation + ".zip");
            CreateSecurityBoundaryArchive(archivePath, mutation);
            var command = $"Import-Module '{module.Replace("'", "''", StringComparison.Ordinal)}' -Force; " +
                $"Test-ReleaseArchive -ArchivePath '{archivePath.Replace("'", "''", StringComparison.Ordinal)}' " +
                $"-TemporaryBase '{fixture.Path.Replace("'", "''", StringComparison.Ordinal)}' -ArchiveKind Update -Version 6.0.0";
            var result = await RunAsync("pwsh", $"-NoProfile -Command \"{command}\"", expectSuccess: false);
            Assert.AreNotEqual(0, result.ExitCode, $"Unsafe archive mutation '{mutation}' was accepted.");
        }
    }

    [TestMethod]
    public async Task FullVerifierRejectsMalformedCommitWithoutEchoingMetadataOrWritingReport()
    {
        using var fixture = new TemporaryDirectory();
        const string pathSentinel = "PRIVATE-PATH-SENTINEL";
        const string tokenSentinel = "PRIVATE-TOKEN-SENTINEL";
        const string environmentSentinel = "PRIVATE-ENV-SENTINEL";
        var malformedCommit = $"bad\n{pathSentinel}\n{tokenSentinel}\n{environmentSentinel}";
        await CreateVerifierMetadataFixtureAsync(fixture.Path, malformedCommit);

        var result = await RunAsync(
            "pwsh",
            $"-NoProfile -File eng/verify-release.ps1 -ReleaseRoot \"{fixture.Path}\" -Version 6.0.0 -Architectures x64",
            expectSuccess: false);

        Assert.AreNotEqual(0, result.ExitCode);
        StringAssert.Contains(result.Error + result.Output, "commit object ID is invalid");
        foreach (var sentinel in new[] { pathSentinel, tokenSentinel, environmentSentinel })
        {
            Assert.IsFalse((result.Error + result.Output).Contains(sentinel, StringComparison.Ordinal));
        }
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Path, "verification-report.json")));
    }

    [TestMethod]
    public async Task AppInstallerGeneratorCreatesStableWindows10Metadata()
    {
        using var fixture = new TemporaryDirectory();
        const string version = "6.0.0";
        const string identityName = "saroo98.CodexUsageMonitor";
        const string publisher = "CN=Codex Usage Monitor Development";
        const string appInstallerUri = "https://github.com/saroo98/codex-usage-monitor/releases/download/v6.0.0/CodexUsageMonitor.appinstaller";
        const string bundleUri = "https://github.com/saroo98/codex-usage-monitor/releases/download/v6.0.0/CodexUsageMonitor-6.0.0.msixbundle";

        await RunAsync(
            "pwsh",
            $"-NoProfile -File eng/generate-appinstaller.ps1 -AppInstallerUri {appInstallerUri} -BundleUri {bundleUri} -Version {version} -IdentityName {identityName} -Publisher \"{publisher}\" -OutputRoot \"{fixture.Path}\"");

        var outputPath = Path.Combine(fixture.Path, "CodexUsageMonitor.appinstaller");
        Assert.IsTrue(File.Exists(outputPath), "The App Installer filename must remain stable across releases.");
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Path, $"CodexUsageMonitor-{version}.appinstaller")));
        var bytes = await File.ReadAllBytesAsync(outputPath);
        Assert.IsFalse(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), "The App Installer file must be UTF-8 without a BOM.");
        var text = Encoding.UTF8.GetString(bytes);
        Assert.IsFalse(text.Contains("@@", StringComparison.Ordinal), "The generated XML must not contain template tokens.");

        var document = XDocument.Parse(text);
        XNamespace ns = "http://schemas.microsoft.com/appx/appinstaller/2018";
        var root = document.Root ?? throw new AssertFailedException("The App Installer document has no root element.");
        Assert.AreEqual(ns + "AppInstaller", root.Name);
        Assert.AreEqual(appInstallerUri, (string?)root.Attribute("Uri"));
        Assert.AreEqual("6.0.0.0", (string?)root.Attribute("Version"));
        var mainBundle = root.Element(ns + "MainBundle") ?? throw new AssertFailedException("MainBundle is missing.");
        Assert.AreEqual(identityName, (string?)mainBundle.Attribute("Name"));
        Assert.AreEqual(publisher, (string?)mainBundle.Attribute("Publisher"));
        Assert.AreEqual("6.0.0.0", (string?)mainBundle.Attribute("Version"));
        Assert.AreEqual(bundleUri, (string?)mainBundle.Attribute("Uri"));
        var updateSettings = root.Element(ns + "UpdateSettings") ?? throw new AssertFailedException("UpdateSettings is missing.");
        Assert.AreEqual("24", (string?)updateSettings.Element(ns + "OnLaunch")?.Attribute("HoursBetweenUpdateChecks"));
        Assert.AreEqual(1, updateSettings.Elements(ns + "AutomaticBackgroundTask").Count());
        Assert.IsNull(updateSettings.Element(ns + "OnLaunch")?.Attribute("ShowPrompt"));
        Assert.IsNull(updateSettings.Element(ns + "OnLaunch")?.Attribute("UpdateBlocksActivation"));
    }

    [TestMethod]
    public async Task AppInstallerGeneratorRejectsUnsafeOrMismatchedUris()
    {
        using var fixture = new TemporaryDirectory();
        const string validAppInstallerUri = "https://github.com/saroo98/codex-usage-monitor/releases/download/v6.0.0/CodexUsageMonitor.appinstaller";
        const string validBundleUri = "https://github.com/saroo98/codex-usage-monitor/releases/download/v6.0.0/CodexUsageMonitor-6.0.0.msixbundle";
        var cases = new (string Name, string AppInstallerUri, string BundleUri)[]
        {
            ("http-appinstaller", validAppInstallerUri.Replace("https://", "http://", StringComparison.Ordinal), validBundleUri),
            ("http-bundle", validAppInstallerUri, validBundleUri.Replace("https://", "http://", StringComparison.Ordinal)),
            ("credentials-appinstaller", validAppInstallerUri.Replace("https://", "https://user:password@", StringComparison.Ordinal), validBundleUri),
            ("credentials-bundle", validAppInstallerUri, validBundleUri.Replace("https://", "https://user:password@", StringComparison.Ordinal)),
            ("fragment-appinstaller", validAppInstallerUri + "#fragment", validBundleUri),
            ("fragment-bundle", validAppInstallerUri, validBundleUri + "#fragment"),
            ("query-suffix-appinstaller", "https://github.com/saroo98/codex-usage-monitor/releases/download/v6.0.0/feed?next=/CodexUsageMonitor.appinstaller", validBundleUri),
            ("query-suffix-bundle", validAppInstallerUri, "https://github.com/saroo98/codex-usage-monitor/releases/download/v6.0.0/feed?next=/CodexUsageMonitor-6.0.0.msixbundle"),
            ("query-after-appinstaller", validAppInstallerUri + "?download=1", validBundleUri),
            ("query-after-bundle", validAppInstallerUri, validBundleUri + "?download=1"),
            ("wrong-appinstaller-filename", validAppInstallerUri.Replace("CodexUsageMonitor.appinstaller", "CodexUsageMonitor-6.0.0.appinstaller", StringComparison.Ordinal), validBundleUri),
            ("wrong-bundle-version", validAppInstallerUri, validBundleUri.Replace("6.0.0.msixbundle", "6.0.1.msixbundle", StringComparison.Ordinal)),
        };

        foreach (var testCase in cases)
        {
            var outputRoot = Path.Combine(fixture.Path, testCase.Name);
            var result = await RunAsync(
                "pwsh",
                $"-NoProfile -File eng/generate-appinstaller.ps1 -AppInstallerUri {testCase.AppInstallerUri} -BundleUri {testCase.BundleUri} -Version 6.0.0 -IdentityName saroo98.CodexUsageMonitor -Publisher \"CN=Codex Usage Monitor Development\" -OutputRoot \"{outputRoot}\"",
                expectSuccess: false);

            Assert.AreNotEqual(0, result.ExitCode, $"The generator accepted the {testCase.Name} URI case.");
            Assert.IsFalse(File.Exists(Path.Combine(outputRoot, "CodexUsageMonitor.appinstaller")),
                $"The generator wrote output for the rejected {testCase.Name} URI case.");
        }
    }

    [TestMethod]
    public async Task PublicUnsignedPackagingRejectsAnythingExceptTheExactX64AndArm64MatrixBeforePublishing()
    {
        using var fixture = new TemporaryDirectory();
        var result = await RunAsync(
            "pwsh",
            $"-NoProfile -File eng/package-release.ps1 -Version 6.0.0 -OutputRoot \"{fixture.Path}\" -Architectures x64 -Configuration Release -ReleaseMode PublicUnsigned -Repository saroo98/codex-usage-monitor -UpdateTrustAnchor AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
            expectSuccess: false);

        Assert.AreNotEqual(0, result.ExitCode);
        StringAssert.Contains(result.Error + result.Output, "exact x64 and arm64 architecture matrix");
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(fixture.Path).Any(),
            "Public packaging must fail before it creates output when the architecture matrix is invalid.");
    }

    [TestMethod]
    public async Task PublicUnsignedPackagingRequiresARealProcessScopedUpdateKeyBeforeCreatingOutput()
    {
        using var fixture = new TemporaryDirectory();
        var outputRoot = Path.Combine(fixture.Path, "release");
        var result = await RunAsync(
            "pwsh",
            $"-NoProfile -Command \"Remove-Item Env:UPDATE_PRIVATE_KEY_BASE64 -ErrorAction SilentlyContinue; & './eng/package-release.ps1' -Version 6.0.0 -OutputRoot '{outputRoot}' -Architectures @('x64','arm64') -Configuration Release -ReleaseMode PublicUnsigned -Repository saroo98/codex-usage-monitor -UpdateTrustAnchor {Rfc8032TrustAnchor}\"",
            expectSuccess: false);

        Assert.AreNotEqual(0, result.ExitCode);
        StringAssert.Contains(result.Error + result.Output, "requires UPDATE_PRIVATE_KEY_BASE64");
        Assert.IsFalse(Directory.Exists(outputRoot));
    }

    [TestMethod]
    public async Task PublicUnsignedVerifierRequiresTheExactPublicAssetMatrix()
    {
        using var fixture = new TemporaryDirectory();
        var result = await RunAsync(
            "pwsh",
            $"-NoProfile -Command \"& './eng/verify-release.ps1' -ReleaseRoot '{fixture.Path}' -Version 6.0.0 -Architectures @('x64','arm64') -ReleaseMode PublicUnsigned -ExpectedRepository saroo98/codex-usage-monitor -UpdateTrustAnchor AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\"",
            expectSuccess: false);

        Assert.AreNotEqual(0, result.ExitCode);
        StringAssert.Contains(result.Error + result.Output, "UNSIGNED-WINDOWS-RELEASE.txt");
    }

    [TestMethod]
    public async Task PublicUnsignedVerifierAcceptsTheExactMatrixAndRejectsAnExtraSigningArtifact()
    {
        using var fixture = new TemporaryDirectory();
        await CreatePublicUnsignedReleaseFixtureAsync(fixture.Path);
        var command = $"-NoProfile -Command \"& './eng/verify-release.ps1' -ReleaseRoot '{fixture.Path}' -Version 6.0.0 -Architectures @('x64','arm64') -ReleaseMode PublicUnsigned -ExpectedRepository saroo98/codex-usage-monitor -UpdateTrustAnchor {Rfc8032TrustAnchor}\"";

        await RunAsync("pwsh", command);

        await File.WriteAllBytesAsync(Path.Combine(fixture.Path, "unexpected-signing-key.pfx"), [1, 2, 3]);
        var rejected = await RunAsync("pwsh", command, expectSuccess: false);
        Assert.AreNotEqual(0, rejected.ExitCode);
        StringAssert.Contains(rejected.Error + rejected.Output, "exact reviewed artifact matrix");
    }

    [TestMethod]
    public async Task PublicUnsignedVerifierRejectsAnSbomThatOmitsDirectSourceDependencies()
    {
        using var fixture = new TemporaryDirectory();
        await CreatePublicUnsignedReleaseFixtureAsync(fixture.Path);
        var bomPath = Path.Combine(fixture.Path, "bom.json");
        var bom = JsonNode.Parse(await File.ReadAllTextAsync(bomPath))!.AsObject();
        bom["components"]!.AsArray().RemoveAt(0);
        await File.WriteAllTextAsync(bomPath, bom.ToJsonString(), new UTF8Encoding(false));
        var bomHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(bomPath)));
        var checksumPath = Path.Combine(fixture.Path, "SHA256SUMS.txt");
        var inventory = (await File.ReadAllLinesAsync(checksumPath))
            .Select(line => line.EndsWith(" *bom.json", StringComparison.Ordinal)
                ? $"{bomHash} *bom.json"
                : line)
            .ToArray();
        await File.WriteAllLinesAsync(checksumPath, inventory, new UTF8Encoding(false));
        var command = $"-NoProfile -Command \"& './eng/verify-release.ps1' -ReleaseRoot '{fixture.Path}' -Version 6.0.0 -Architectures @('x64','arm64') -ReleaseMode PublicUnsigned -ExpectedRepository saroo98/codex-usage-monitor -UpdateTrustAnchor {Rfc8032TrustAnchor}\"";

        var rejected = await RunAsync("pwsh", command, expectSuccess: false);

        Assert.AreNotEqual(0, rejected.ExitCode);
        StringAssert.Contains(rejected.Error + rejected.Output, "SBOM dependency coverage is incomplete");
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
        StringAssert.Contains(orchestrator, "publish-release-trees.ps1");
        var buildProperties = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        StringAssert.Contains(buildProperties, "ReleasePackagingRestore");
        StringAssert.Contains(buildProperties, "NuGetLockFilePath");
        var manifest = File.ReadAllText(Path.Combine(root, "packaging", "templates", "msix", "AppxManifest.xml"));
        StringAssert.Contains(manifest, "@@PACKAGE_VERSION@@");
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

    private static async Task CreatePublicUnsignedReleaseFixtureAsync(string root)
    {
        const string version = "6.0.0";
        foreach (var architecture in new[] { "arm64", "x64" })
        {
            CreateUpdateArchive(Path.Combine(root, $"CodexUsageMonitor-{version}-win-{architecture}-update.zip"), unsafePath: false);
            foreach (var flavor in new[] { "framework-dependent", "self-contained" })
            {
                CreatePortableArchive(Path.Combine(root, $"CodexUsageMonitor-{version}-win-{architecture}-portable-{flavor}.zip"));
            }
        }

        var sourceArchive = Path.Combine(root, $"CodexUsageMonitor-{version}-source.zip");
        await RunAsync("git", $"archive --format=zip --output=\"{sourceArchive}\" --prefix=CodexUsageMonitor-{version}/ HEAD");
        var head = (await RunAsync("git", "rev-parse HEAD")).Output.Trim();

        await File.WriteAllTextAsync(Path.Combine(root, "LICENSE.txt"), "license", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(root, "THIRD-PARTY-NOTICES.md"), "notices", new UTF8Encoding(false));
        await File.WriteAllTextAsync(
            Path.Combine(root, "UNSIGNED-WINDOWS-RELEASE.txt"),
            "UNSIGNED WINDOWS RELEASE\n" +
            "These Windows executables are not Authenticode-signed and Windows can show an unverified or unknown publisher.\n" +
            "Verify downloads against SHA256SUMS.txt and the GitHub artifact attestations from saroo98/codex-usage-monitor.\n" +
            "Do not disable Windows security controls.\n",
            new UTF8Encoding(false));
        await File.WriteAllTextAsync(
            Path.Combine(root, "bom.json"),
            JsonSerializer.Serialize(new
            {
                bomFormat = "CycloneDX",
                metadata = new { component = new { version } },
                components = GetDirectSourcePackageNames().Select(name => new { name }).ToArray(),
            }),
            new UTF8Encoding(false));
        await File.WriteAllTextAsync(
            Path.Combine(root, "BUILD-METADATA.json"),
            JsonSerializer.Serialize(new
            {
                product = "Codex Usage Monitor for Windows",
                version,
                commit = head,
                sdk = "10.0.302",
                configuration = "Release",
                architectures = new[] { "arm64", "x64" },
                releaseMode = "public-unsigned",
                windowsAuthenticode = false,
                attestationProvider = "GitHub Actions",
                generatedAtUtc = "2026-08-11T00:00:00.0000000+00:00",
            }),
            new UTF8Encoding(false));

        var assets = new List<object>();
        foreach (var architecture in new[] { "arm64", "x64" })
        {
            var fileName = $"CodexUsageMonitor-{version}-win-{architecture}-update.zip";
            var path = Path.Combine(root, fileName);
            assets.Add(new
            {
                architecture,
                url = $"https://github.com/saroo98/codex-usage-monitor/releases/download/v{version}/{fileName}",
                fileName,
                sizeBytes = new FileInfo(path).Length,
                sha256 = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))),
                publisherThumbprints = Array.Empty<string>(),
            });
        }
        var manifestPath = Path.Combine(root, "update-manifest.json");
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                channel = "stable",
                version,
                publishedAtUtc = DateTimeOffset.UtcNow,
                minimumOsBuild = 19041,
                releaseNotesUrl = $"https://github.com/saroo98/codex-usage-monitor/releases/tag/v{version}",
                assets,
                signature = string.Empty,
            }),
            new UTF8Encoding(false));
        var privateKeyPath = Path.Combine(root, "fixture-private.key");
        await File.WriteAllBytesAsync(privateKeyPath, Convert.FromHexString(
            "4CCD089B28FF96DA9DB6C346EC114E0F" +
            "5B8A319F35ABA624DA8CF6ED4FB8A6FB"));
        await RunAsync(
            "dotnet",
            $"run --project tools/CodexUsageMonitor.ReleaseTool/CodexUsageMonitor.ReleaseTool.csproj --configuration Debug -- sign --manifest \"{manifestPath}\" --private-key \"{privateKeyPath}\" --trust-anchor {Rfc8032TrustAnchor}");
        File.Delete(privateKeyPath);

        var inventory = Directory.EnumerateFiles(root)
            .Where(path => !string.Equals(Path.GetFileName(path), "SHA256SUMS.txt", StringComparison.Ordinal))
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .Select(path => $"{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))} *{Path.GetFileName(path)}")
            .ToArray();
        await File.WriteAllLinesAsync(Path.Combine(root, "SHA256SUMS.txt"), inventory, new UTF8Encoding(false));
    }

    private static string[] GetDirectSourcePackageNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(
                     Path.Combine(RepositoryRoot(), "src"),
                     "packages.lock.json",
                     SearchOption.AllDirectories))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var framework in document.RootElement.GetProperty("dependencies").EnumerateObject())
            {
                foreach (var dependency in framework.Value.EnumerateObject())
                {
                    if (dependency.Value.TryGetProperty("type", out var type) &&
                        string.Equals(type.GetString(), "Direct", StringComparison.Ordinal))
                    {
                        names.Add(dependency.Name);
                    }
                }
            }
        }

        return names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void CreatePortableArchive(string path)
    {
        var files = new Dictionary<string, byte[]>
        {
            ["CodexUsageMonitor.exe"] = [1, 2, 3],
            ["CodexUsageMonitor.UpdaterHost.exe"] = [4, 5, 6],
            ["README.md"] = Encoding.UTF8.GetBytes("readme"),
        };
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            version = "6.0.0",
            files = files.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new
            {
                path = pair.Key,
                sizeBytes = pair.Value.Length,
                sha256 = Convert.ToHexStringLower(SHA256.HashData(pair.Value)),
            }).ToArray(),
        });
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var pair in files)
        {
            var entry = archive.CreateEntry("CodexUsageMonitor/" + pair.Key);
            using var stream = entry.Open();
            stream.Write(pair.Value);
        }
        var manifestEntry = archive.CreateEntry("CodexUsageMonitor/update-files.json");
        using (var stream = manifestEntry.Open()) { stream.Write(manifest); }
        foreach (var name in new[] { "INSTALL.txt", "UNINSTALL.txt", "portable.mode" })
        {
            var entry = archive.CreateEntry("CodexUsageMonitor/" + name);
            using var stream = entry.Open();
            stream.WriteByte(0);
        }
    }

    private static void CreateSecurityBoundaryArchive(string path, string mutation)
    {
        var files = new Dictionary<string, byte[]>
        {
            ["CodexUsageMonitor.exe"] = [1],
            ["CodexUsageMonitor.UpdaterHost.exe"] = [2],
        };
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            version = "6.0.0",
            files = files.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new
            {
                path = pair.Key,
                sizeBytes = pair.Value.Length,
                sha256 = Convert.ToHexStringLower(SHA256.HashData(pair.Value)),
            }).ToArray(),
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
        if (mutation == "valid") { return; }
        var name = mutation switch
        {
            "traversal" => "../escape.txt",
            "duplicate" => "codexusagemonitor.exe",
            "data" => "data/settings.json",
            "marker" => "UNSIGNED-RELEASE-CANDIDATE.txt",
            _ => "link",
        };
        var unsafeEntry = archive.CreateEntry(name);
        if (mutation == "reparse") { unsafeEntry.ExternalAttributes = unchecked((int)0xA1FF0000); }
        using var unsafeStream = unsafeEntry.Open();
        unsafeStream.WriteByte(3);
    }

    private static async Task CreateVerifierMetadataFixtureAsync(string root, string commit)
    {
        var metadata = JsonSerializer.Serialize(new
        {
            product = "Codex Usage Monitor for Windows",
            version = "6.0.0",
            commit,
            sdk = "10.0.100",
            configuration = "Release",
            architectures = new[] { "x64" },
            production = false,
            generatedAtUtc = "2026-08-10T00:00:00.0000000+00:00",
        });
        var files = new Dictionary<string, string>
        {
            ["BUILD-METADATA.json"] = metadata,
            ["THIRD-PARTY-NOTICES.md"] = "notices",
            ["LICENSE.txt"] = "license",
            ["bom.json"] = "{}",
            ["update-manifest.json"] = "{}",
            ["CodexUsageMonitor-6.0.0-source.zip"] = "placeholder",
            ["CodexUsageMonitor-6.0.0-win-x64-portable-framework-dependent.zip"] = "placeholder",
            ["CodexUsageMonitor-6.0.0-win-x64-portable-self-contained.zip"] = "placeholder",
            ["CodexUsageMonitor-6.0.0-win-x64-update.zip"] = "placeholder",
            ["CodexUsageMonitor-6.0.0-x64.msix"] = "placeholder",
            ["UNSIGNED-RELEASE-CANDIDATE.txt"] = "UNSIGNED VALIDATION ARTIFACTS\nThese files are not production-signed. Do not publish or distribute them as a release.\n",
        };
        foreach (var (name, content) in files)
        {
            await File.WriteAllTextAsync(Path.Combine(root, name), content, new UTF8Encoding(false));
        }
        var inventory = files.Select(pair =>
            $"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(pair.Value)))} *{pair.Key}");
        await File.WriteAllLinesAsync(Path.Combine(root, "SHA256SUMS.txt"), inventory, new UTF8Encoding(false));
    }

    private static async Task<TemporaryDirectory> CreatePublicationAuditFixtureAsync()
    {
        var fixture = new TemporaryDirectory();
        try
        {
            var eng = Directory.CreateDirectory(Path.Combine(fixture.Path, "eng")).FullName;
            File.Copy(
                Path.Combine(RepositoryRoot(), "eng", "audit-publication.py"),
                Path.Combine(eng, "audit-publication.py"));
            await RunAsync("git", "init --quiet", workingDirectory: fixture.Path);
            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    private static SortedDictionary<string, string> HashFirstPartyPortableBinaries(string root)
    {
        return new SortedDictionary<string, string>(
            Directory.EnumerateFiles(root, "CodexUsageMonitor*", SearchOption.TopDirectoryOnly)
                .Where(path => string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(
                path => Path.GetFileName(path)!,
                path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),
                StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    private static async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        bool expectSuccess = true,
        string? workingDirectory = null)
    {
        var start = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory ?? RepositoryRoot(),
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
