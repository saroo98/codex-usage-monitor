using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexUsageMonitor.PackagingTests;

[TestClass]
public sealed class ReleaseToolTests
{
    private const string PrivateKeyHex = "9D61B19DEFFD5A60BA844AF492EC2CC4" + "4449C5697B326919703BAC031CAE7F60";
    private const string TrustAnchor = "11qYAYKxCrfVS/7TyWQHOg7hcvPapiMlrwIaaPcHURo=";
    private static readonly string OtherTrustAnchor = Convert.ToBase64String(Convert.FromHexString(
        "D65A980182B10AB7D54BFED3C964073A" + "0EE172F3DAA62325AF021A68F707511A"));
    private const string PrivateKeyEnvironmentName = "CODEX_USAGE_MONITOR_TEST_PRIVATE_KEY";

    [TestMethod]
    public async Task EnvironmentPrivateKeySignsTheRfc8032VectorWithoutPrintingIt()
    {
        using var fixture = new TemporaryDirectory();
        var manifest = await CreateManifestAsync(fixture.Path);
        var privateKey = Convert.ToBase64String(Convert.FromHexString(PrivateKeyHex));

        var result = await RunToolAsync(
            $"sign --manifest \"{manifest}\" --trust-anchor {TrustAnchor} --private-key-env {PrivateKeyEnvironmentName}",
            new Dictionary<string, string?> { [PrivateKeyEnvironmentName] = privateKey });

        Assert.AreEqual(0, result.ExitCode, result.CombinedOutput);
        Assert.IsFalse(result.CombinedOutput.Contains(privateKey, StringComparison.Ordinal));
        var signedBytes = await File.ReadAllBytesAsync(manifest);
        Assert.IsFalse(signedBytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        var signedText = Encoding.UTF8.GetString(signedBytes);
        StringAssert.Contains(signedText, "\n  \"schemaVersion\"");
        using var signed = JsonDocument.Parse(signedText);
        var signature = signed.RootElement.GetProperty("signature").GetString();
        Assert.AreEqual(64, Convert.FromBase64String(signature!).Length);
    }

    [TestMethod]
    public async Task MissingEnvironmentPrivateKeyFailsSafelyWithExitCodeTwo()
    {
        using var fixture = new TemporaryDirectory();
        var result = await RunToolAsync(
            $"sign --manifest \"{await CreateManifestAsync(fixture.Path)}\" --trust-anchor {TrustAnchor} --private-key-env {PrivateKeyEnvironmentName}",
            new Dictionary<string, string?> { [PrivateKeyEnvironmentName] = null });

        Assert.AreEqual(2, result.ExitCode, result.CombinedOutput);
        StringAssert.Contains(result.CombinedOutput, PrivateKeyEnvironmentName);
    }

    [TestMethod]
    public async Task InvalidEnvironmentPrivateKeyLengthFailsSafelyWithExitCodeTwo()
    {
        using var fixture = new TemporaryDirectory();
        var invalidKey = Convert.ToBase64String(new byte[31]);
        var result = await RunToolAsync(
            $"sign --manifest \"{await CreateManifestAsync(fixture.Path)}\" --trust-anchor {TrustAnchor} --private-key-env {PrivateKeyEnvironmentName}",
            new Dictionary<string, string?> { [PrivateKeyEnvironmentName] = invalidKey });

        Assert.AreEqual(2, result.ExitCode, result.CombinedOutput);
        StringAssert.Contains(result.CombinedOutput, "32");
        Assert.IsFalse(result.CombinedOutput.Contains(invalidKey, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task EnvironmentPrivateKeyTrustAnchorMismatchFailsSafelyWithExitCodeTwo()
    {
        using var fixture = new TemporaryDirectory();
        var privateKey = Convert.ToBase64String(Convert.FromHexString(PrivateKeyHex));
        var result = await RunToolAsync(
            $"sign --manifest \"{await CreateManifestAsync(fixture.Path)}\" --trust-anchor {OtherTrustAnchor} --private-key-env {PrivateKeyEnvironmentName}",
            new Dictionary<string, string?> { [PrivateKeyEnvironmentName] = privateKey });

        Assert.AreEqual(2, result.ExitCode, result.CombinedOutput);
        StringAssert.Contains(result.CombinedOutput, "does not match");
        Assert.IsFalse(result.CombinedOutput.Contains(privateKey, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task FileAndEnvironmentPrivateKeyInputsAreMutuallyExclusive()
    {
        using var fixture = new TemporaryDirectory();
        var privateKeyPath = Path.Combine(fixture.Path, "private.key");
        await File.WriteAllBytesAsync(privateKeyPath, Convert.FromHexString(PrivateKeyHex));
        var result = await RunToolAsync(
            $"sign --manifest \"{await CreateManifestAsync(fixture.Path)}\" --trust-anchor {TrustAnchor} --private-key \"{privateKeyPath}\" --private-key-env {PrivateKeyEnvironmentName}",
            new Dictionary<string, string?> { [PrivateKeyEnvironmentName] = Convert.ToBase64String(Convert.FromHexString(PrivateKeyHex)) });

        Assert.AreEqual(2, result.ExitCode, result.CombinedOutput);
        StringAssert.Contains(result.CombinedOutput, "mutually exclusive");
    }

    [TestMethod]
    public async Task OperationSpecificParsingRejectsMalformedOrUnsupportedArguments()
    {
        using var fixture = new TemporaryDirectory();
        var manifest = await CreateManifestAsync(fixture.Path);
        var privateKeyPath = Path.Combine(fixture.Path, "private.key");
        await File.WriteAllBytesAsync(privateKeyPath, Convert.FromHexString(PrivateKeyHex));
        var cases = new (string Arguments, string Expected)[]
        {
            ($"sign --manifest \"{manifest}\" --trust-anchor {TrustAnchor} --private-key \"{privateKeyPath}\" --unexpected value", "Unknown argument"),
            ($"sign --manifest \"{manifest}\" --manifest \"{manifest}\" --trust-anchor {TrustAnchor} --private-key \"{privateKeyPath}\"", "Duplicate argument"),
            ($"sign --manifest \"{manifest}\" --trust-anchor {TrustAnchor} --private-key", "named key/value pairs"),
            ($"sign --manifest \"{manifest}\" --trust-anchor {TrustAnchor}", "private key source"),
            ($"validate-keypair --trust-anchor {TrustAnchor}", "private key source"),
            ($"verify --manifest \"{manifest}\" --trust-anchor {TrustAnchor} --private-key \"{privateKeyPath}\"", "does not accept a private key source"),
            ("unknown-operation --value ignored", "Usage:"),
        };

        foreach (var testCase in cases)
        {
            var result = await RunToolAsync(testCase.Arguments);
            Assert.AreEqual(2, result.ExitCode, $"Arguments: {testCase.Arguments}{Environment.NewLine}{result.CombinedOutput}");
            StringAssert.Contains(result.CombinedOutput, testCase.Expected, $"Arguments: {testCase.Arguments}");
        }
    }

    [TestMethod]
    public async Task EnvironmentPrivateKeyValidatesTheRfc8032KeypairWithoutPrintingIt()
    {
        var privateKey = Convert.ToBase64String(Convert.FromHexString(PrivateKeyHex));

        var result = await RunToolAsync(
            $"validate-keypair --trust-anchor {TrustAnchor} --private-key-env {PrivateKeyEnvironmentName}",
            new Dictionary<string, string?> { [PrivateKeyEnvironmentName] = privateKey });

        Assert.AreEqual(0, result.ExitCode, result.CombinedOutput);
        Assert.IsFalse(result.CombinedOutput.Contains(privateKey, StringComparison.Ordinal));
    }

    [TestMethod]
    public Task GenerateKeypairRefusesToOverwriteAnExistingPrivateOutput() => GenerateKeypairRefusesToOverwriteAsync("private.key");

    [TestMethod]
    public Task GenerateKeypairRefusesToOverwriteAnExistingPublicOutput() => GenerateKeypairRefusesToOverwriteAsync("public.txt");

    [TestMethod]
    public async Task GenerateKeypairCleansUpPrivateOutputWhenSecondCreateNewFails()
    {
        using var fixture = new TemporaryDirectory();
        var sharedOutput = Path.Combine(fixture.Path, "shared-output");

        var result = await RunToolAsync($"generate-keypair --private-key-output \"{sharedOutput}\" --public-key-output \"{sharedOutput}\"");

        Assert.AreEqual(2, result.ExitCode, result.CombinedOutput);
        StringAssert.Contains(result.CombinedOutput, "already exists");
        Assert.IsFalse(File.Exists(sharedOutput));
    }

    private static async Task GenerateKeypairRefusesToOverwriteAsync(string existingFile)
    {
        using var fixture = new TemporaryDirectory();
        var privateKey = Path.Combine(fixture.Path, "private.key");
        var publicKey = Path.Combine(fixture.Path, "public.txt");
        await File.WriteAllTextAsync(Path.Combine(fixture.Path, existingFile), "existing");

        var result = await RunToolAsync($"generate-keypair --private-key-output \"{privateKey}\" --public-key-output \"{publicKey}\"");

        Assert.AreEqual(2, result.ExitCode, result.CombinedOutput);
        StringAssert.Contains(result.CombinedOutput, "already exists");
        Assert.AreEqual(existingFile == "private.key", File.Exists(privateKey));
        Assert.AreEqual(existingFile == "public.txt", File.Exists(publicKey));
    }

    [TestMethod]
    public async Task GeneratedKeypairValidatesAndFixtureDeletesItsFiles()
    {
        string privateKey;
        string publicKey;
        using (var fixture = new TemporaryDirectory())
        {
            privateKey = Path.Combine(fixture.Path, "private.key");
            publicKey = Path.Combine(fixture.Path, "public.txt");
            var generated = await RunToolAsync($"generate-keypair --private-key-output \"{privateKey}\" --public-key-output \"{publicKey}\"");
            Assert.AreEqual(0, generated.ExitCode, generated.CombinedOutput);
            Assert.AreEqual(32, (await File.ReadAllBytesAsync(privateKey)).Length);
            var trustAnchor = (await File.ReadAllTextAsync(publicKey)).Trim();
            StringAssert.Contains(generated.Output, trustAnchor);
            StringAssert.Contains(generated.Output, privateKey);
            StringAssert.Contains(generated.Output, publicKey);
            var validated = await RunToolAsync($"validate-keypair --trust-anchor {trustAnchor} --private-key \"{privateKey}\"");
            Assert.AreEqual(0, validated.ExitCode, validated.CombinedOutput);
            Assert.IsFalse(generated.CombinedOutput.Contains(Convert.ToBase64String(await File.ReadAllBytesAsync(privateKey)), StringComparison.Ordinal));
        }

        Assert.IsFalse(File.Exists(privateKey));
        Assert.IsFalse(File.Exists(publicKey));
    }

    [TestMethod]
    public async Task OfflineKeyWrapperRejectsOutputInsideTheRepository()
    {
        var outputDirectory = Path.Combine(RepositoryRoot(), "artifacts", $"release-key-test-{Guid.NewGuid():N}");

        var result = await RunPowerShellAsync(
            Path.Combine(RepositoryRoot(), "eng", "new-update-signing-key.ps1"),
            "-OutputDirectory",
            outputDirectory);

        Assert.AreNotEqual(0, result.ExitCode, result.CombinedOutput);
        StringAssert.Contains(result.CombinedOutput, "outside the repository");
        Assert.IsFalse(Directory.Exists(outputDirectory));
    }

    [TestMethod]
    public async Task OfflineKeyWrapperRejectsExternalJunctionTargetingRepositoryBeforeGeneration()
    {
        using var externalFixture = new TemporaryDirectory();
        using var shimDirectory = new TemporaryDirectory();
        var repositoryTarget = Path.Combine(RepositoryRoot(), "artifacts", $"release-key-junction-target-{Guid.NewGuid():N}");
        var junction = Path.Combine(externalFixture.Path, "outside-junction");
        var marker = Path.Combine(shimDirectory.Path, "dotnet-invoked");
        var privateKeyPath = Path.Combine(repositoryTarget, "codex-usage-monitor-update-ed25519.key");
        var publicKeyPath = Path.Combine(repositoryTarget, "codex-usage-monitor-update-ed25519-public.txt");
        Directory.CreateDirectory(repositoryTarget);
        await CreateDirectoryJunctionAsync(junction, repositoryTarget);
        await File.WriteAllTextAsync(Path.Combine(shimDirectory.Path, "dotnet.cmd"), """
            @echo off
            type nul > "%TEST_DOTNET_MARKER%"
            exit /b 23
            """);
        var environment = new Dictionary<string, string?>
        {
            ["PATH"] = shimDirectory.Path + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            ["TEST_DOTNET_MARKER"] = marker,
        };

        try
        {
            var result = await RunPowerShellAsync(
                Path.Combine(RepositoryRoot(), "eng", "new-update-signing-key.ps1"),
                environment,
                "-OutputDirectory",
                junction);

            Assert.AreNotEqual(0, result.ExitCode, result.CombinedOutput);
            StringAssert.Contains(result.CombinedOutput, "outside the repository");
            Assert.IsFalse(File.Exists(marker), "ReleaseTool generation must not be invoked for a repository-targeting junction.");
            Assert.IsFalse(File.Exists(privateKeyPath));
            Assert.IsFalse(File.Exists(publicKeyPath));
        }
        finally
        {
            if (Directory.Exists(junction)) { Directory.Delete(junction); }
            if (Directory.Exists(repositoryTarget)) { Directory.Delete(repositoryTarget, recursive: true); }
        }
    }

    [TestMethod]
    public async Task OfflineKeyWrapperGeneratesAndValidatesOnlyCallerRequestedFiles()
    {
        using var fixture = new TemporaryDirectory();
        var privateKeyPath = Path.Combine(fixture.Path, "codex-usage-monitor-update-ed25519.key");
        var publicKeyPath = Path.Combine(fixture.Path, "codex-usage-monitor-update-ed25519-public.txt");

        var result = await RunPowerShellAsync(
            Path.Combine(RepositoryRoot(), "eng", "new-update-signing-key.ps1"),
            "-OutputDirectory",
            fixture.Path);

        Assert.AreEqual(0, result.ExitCode, result.CombinedOutput);
        Assert.AreEqual(32, (await File.ReadAllBytesAsync(privateKeyPath)).Length);
        var trustAnchor = (await File.ReadAllTextAsync(publicKeyPath)).Trim();
        StringAssert.Contains(result.Output, $"UPDATE_TRUST_ANCHOR={trustAnchor}");
        StringAssert.Contains(result.Output, privateKeyPath);
        Assert.IsFalse(result.CombinedOutput.Contains(Convert.ToBase64String(await File.ReadAllBytesAsync(privateKeyPath)), StringComparison.Ordinal));
        CollectionAssert.AreEquivalent(
            new[] { Path.GetFileName(privateKeyPath), Path.GetFileName(publicKeyPath) },
            Directory.GetFiles(fixture.Path).Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public async Task OfflineKeyWrapperCleansUpGeneratedFilesWhenValidationFails()
    {
        using var fixture = new TemporaryDirectory();
        using var shimDirectory = new TemporaryDirectory();
        var privateKeyPath = Path.Combine(fixture.Path, "codex-usage-monitor-update-ed25519.key");
        var publicKeyPath = Path.Combine(fixture.Path, "codex-usage-monitor-update-ed25519-public.txt");
        await File.WriteAllTextAsync(Path.Combine(shimDirectory.Path, "dotnet.cmd"), """
            @echo off
            echo %* | %SystemRoot%\System32\findstr.exe /C:"generate-keypair" >nul
            if errorlevel 1 exit /b 7
            type nul > "%TEST_PRIVATE_OUTPUT%"
            > "%TEST_PUBLIC_OUTPUT%" echo AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=
            exit /b 0
            """);
        var environment = new Dictionary<string, string?>
        {
            ["PATH"] = shimDirectory.Path + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            ["TEST_PRIVATE_OUTPUT"] = privateKeyPath,
            ["TEST_PUBLIC_OUTPUT"] = publicKeyPath,
        };

        var result = await RunPowerShellAsync(
            Path.Combine(RepositoryRoot(), "eng", "new-update-signing-key.ps1"),
            environment,
            "-OutputDirectory",
            fixture.Path);

        Assert.AreNotEqual(0, result.ExitCode, result.CombinedOutput);
        Assert.IsFalse(File.Exists(privateKeyPath));
        Assert.IsFalse(File.Exists(publicKeyPath));
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task ConfigureUpdateSigningSendsOnlyThePrivateKeyThroughGhStandardInput()
    {
        using var fixture = new TemporaryDirectory();
        using var shimDirectory = new TemporaryDirectory();
        var backupDirectory = Path.Combine(fixture.Path, "private backup");
        var capturedSecret = Path.Combine(fixture.Path, "captured-secret.txt");
        var capturedArguments = Path.Combine(fixture.Path, "captured-arguments.txt");
        await File.WriteAllTextAsync(Path.Combine(shimDirectory.Path, "gh.cmd"), """
            @echo off
            if "%1"=="auth" exit /b 0
            if "%1"=="secret" (
              echo %* > "%GH_ARGUMENT_LOG%"
              more > "%GH_SECRET_STDIN%"
              exit /b 0
            )
            exit /b 9
            """, new UTF8Encoding(false));
        var environment = new Dictionary<string, string?>
        {
            ["PATH"] = shimDirectory.Path + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            ["GH_SECRET_STDIN"] = capturedSecret,
            ["GH_ARGUMENT_LOG"] = capturedArguments,
        };
        var trustAnchorPath = Path.Combine(RepositoryRoot(), "packaging", "update", "update-trust-anchor.txt");
        var originalTrustAnchor = File.Exists(trustAnchorPath)
            ? await File.ReadAllBytesAsync(trustAnchorPath)
            : null;
        if (originalTrustAnchor is not null) File.Delete(trustAnchorPath);
        try
        {
            var result = await RunPowerShellAsync(
                Path.Combine(RepositoryRoot(), "eng", "configure-update-signing.ps1"),
                environment,
                "-Repository", "saroo98/codex-usage-monitor",
                "-EnvironmentName", "native-production-release",
                "-PrivateBackupDirectory", backupDirectory);

            Assert.AreEqual(0, result.ExitCode, result.CombinedOutput);
            var privateKey = await File.ReadAllBytesAsync(Path.Combine(backupDirectory, "codex-usage-monitor-update-ed25519.key"));
            var privateBase64 = Convert.ToBase64String(privateKey);
            Assert.AreEqual(privateBase64, (await File.ReadAllTextAsync(capturedSecret)).Trim());
            Assert.IsFalse((await File.ReadAllTextAsync(capturedArguments)).Contains(privateBase64, StringComparison.Ordinal));
            Assert.IsFalse(result.CombinedOutput.Contains(privateBase64, StringComparison.Ordinal));
            Assert.IsTrue(File.Exists(trustAnchorPath));
            Assert.AreEqual(32, Convert.FromBase64String((await File.ReadAllTextAsync(trustAnchorPath)).Trim()).Length);
        }
        finally
        {
            if (File.Exists(trustAnchorPath)) File.Delete(trustAnchorPath);
            if (originalTrustAnchor is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(trustAnchorPath)!);
                await File.WriteAllBytesAsync(trustAnchorPath, originalTrustAnchor);
            }
            else
            {
                var directory = Path.GetDirectoryName(trustAnchorPath)!;
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task ConfigureUpdateSigningCleansGeneratedFilesWhenGitHubRejectsTheSecretWrite()
    {
        using var fixture = new TemporaryDirectory();
        using var shimDirectory = new TemporaryDirectory();
        var backupDirectory = Path.Combine(fixture.Path, "private backup");
        await File.WriteAllTextAsync(Path.Combine(shimDirectory.Path, "gh.cmd"), """
            @echo off
            if "%1"=="auth" exit /b 0
            if "%1"=="secret" (
              more > nul
              exit /b 23
            )
            exit /b 9
            """, new UTF8Encoding(false));
        var environment = new Dictionary<string, string?>
        {
            ["PATH"] = shimDirectory.Path + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
        };
        var trustAnchorPath = Path.Combine(RepositoryRoot(), "packaging", "update", "update-trust-anchor.txt");
        var originalTrustAnchor = File.Exists(trustAnchorPath)
            ? await File.ReadAllBytesAsync(trustAnchorPath)
            : null;
        if (originalTrustAnchor is not null) File.Delete(trustAnchorPath);
        try
        {
            var result = await RunPowerShellAsync(
                Path.Combine(RepositoryRoot(), "eng", "configure-update-signing.ps1"),
                environment,
                "-Repository", "saroo98/codex-usage-monitor",
                "-EnvironmentName", "native-production-release",
                "-PrivateBackupDirectory", backupDirectory);

            Assert.AreNotEqual(0, result.ExitCode);
            StringAssert.Contains(result.CombinedOutput, "rejected the update signing secret write");
            Assert.IsFalse(File.Exists(trustAnchorPath));
            Assert.IsFalse(File.Exists(Path.Combine(backupDirectory, "codex-usage-monitor-update-ed25519.key")));
            Assert.IsFalse(File.Exists(Path.Combine(backupDirectory, "codex-usage-monitor-update-ed25519-public.txt")));
        }
        finally
        {
            if (originalTrustAnchor is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(trustAnchorPath)!);
                await File.WriteAllBytesAsync(trustAnchorPath, originalTrustAnchor);
            }
        }
    }

    private static async Task<string> CreateManifestAsync(string directory)
    {
        var path = Path.Combine(directory, "update-manifest.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            channel = "stable",
            version = "6.0.0",
            publishedAtUtc = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero),
            minimumOsBuild = 19041,
            releaseNotesUrl = "https://example.invalid/releases/6.0.0",
            assets = Array.Empty<object>(),
            signature = string.Empty,
        }));
        return path;
    }

    private static async Task<ProcessResult> RunToolAsync(string commandArguments, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var start = new ProcessStartInfo("dotnet", $"run --project tools/CodexUsageMonitor.ReleaseTool/CodexUsageMonitor.ReleaseTool.csproj --configuration Debug --no-restore -- {commandArguments}")
        {
            WorkingDirectory = RepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                if (value is null) { start.Environment.Remove(name); }
                else { start.Environment[name] = value; }
            }
        }
        using var process = Process.Start(start) ?? throw new AssertFailedException("Could not start the release tool.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        return new ProcessResult(process.ExitCode, await output, await error);
    }

    private static async Task<ProcessResult> RunPowerShellAsync(string scriptPath, params string[] arguments)
        => await RunPowerShellAsync(scriptPath, null, arguments);

    private static async Task CreateDirectoryJunctionAsync(string junction, string target)
    {
        var start = new ProcessStartInfo("pwsh")
        {
            WorkingDirectory = RepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$null = New-Item -ItemType Junction -Path $env:TEST_JUNCTION_PATH -Target $env:TEST_JUNCTION_TARGET");
        start.Environment["TEST_JUNCTION_PATH"] = junction;
        start.Environment["TEST_JUNCTION_TARGET"] = target;

        using var process = Process.Start(start) ?? throw new AssertFailedException("Could not start PowerShell to create a test junction.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(1));
        Assert.AreEqual(0, process.ExitCode, await output + await error);
    }

    private static async Task<ProcessResult> RunPowerShellAsync(
        string scriptPath,
        IReadOnlyDictionary<string, string?>? environment,
        params string[] arguments)
    {
        var start = new ProcessStartInfo("pwsh")
        {
            WorkingDirectory = RepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                if (value is null) { start.Environment.Remove(name); }
                else { start.Environment[name] = value; }
            }
        }

        using var process = Process.Start(start) ?? throw new AssertFailedException("Could not start PowerShell.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        return new ProcessResult(process.ExitCode, await output, await error);
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Directory.Build.props"))) { current = current.Parent; }
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error)
    {
        public string CombinedOutput => Output + Error;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"CodexUsageMonitorReleaseTool-{Guid.NewGuid():N}");
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
