using System.Diagnostics;
using System.Text;

namespace CodexUsageMonitor.PackagingTests;

[TestClass]
public sealed class PublicReleasePreflightTests
{
    private const string Version = "6.0.0";
    private const string WorkflowPath = ".github/workflows/native-public-release.yml";

    [TestMethod]
    public async Task PublicContextAcceptsLightweightAndAnnotatedTags()
    {
        await using var fixture = await CreateFixtureAsync();
        foreach (var annotated in new[] { false, true })
        {
            await GitAsync(fixture.Root, "tag", "-d", "v6.0.0");
            if (annotated)
            {
                await GitAsync(fixture.Root, "tag", "-a", "v6.0.0", "-m", "fixture tag");
            }
            else
            {
                await GitAsync(fixture.Root, "tag", "v6.0.0");
            }

            var result = await RunContextAsync(fixture);
            Assert.AreEqual(0, result.ExitCode, result.Combined);
        }
    }

    [TestMethod]
    public async Task PublicContextRejectsWrongTagBranchWorkflowAndWorkflowSha()
    {
        await using var fixture = await CreateFixtureAsync();
        var baseline = ContextEnvironment(fixture);
        var cases = new (string Name, string? Value)[]
        {
            ("GITHUB_REF", "refs/heads/main"),
            ("GITHUB_REF_TYPE", "branch"),
            ("GITHUB_REF_NAME", "v6.0.0-wrong"),
            ("GITHUB_WORKFLOW_REF", $"saroo98/codex-usage-monitor/{WorkflowPath}@refs/tags/v6.0.0-wrong"),
            ("GITHUB_WORKFLOW_SHA", new string('0', 40)),
        };

        foreach (var invalid in cases)
        {
            var environment = new Dictionary<string, string?>(baseline, StringComparer.OrdinalIgnoreCase)
            {
                [invalid.Name] = invalid.Value,
            };
            var result = await RunScriptAsync(fixture.Root, "eng/assert-public-release-context.ps1", environment);
            Assert.AreEqual(1, result.ExitCode, $"{invalid.Name} must fail closed. {result.Combined}");
        }
    }

    [TestMethod]
    public async Task PublicContextRejectsMissingTaggedWorkflowDirtyTreeAndUnrelatedMain()
    {
        await using var fixture = await CreateFixtureAsync();

        File.AppendAllText(Path.Combine(fixture.Root, "README.md"), "dirty", Encoding.UTF8);
        var dirty = await RunContextAsync(fixture);
        Assert.AreEqual(1, dirty.ExitCode, dirty.Combined);
        await GitAsync(fixture.Root, "restore", "README.md");

        File.Delete(Path.Combine(fixture.Root, WorkflowPath.Replace('/', Path.DirectorySeparatorChar)));
        await GitAsync(fixture.Root, "add", "--all");
        await GitAsync(fixture.Root, "commit", "--quiet", "-m", "remove workflow");
        await GitAsync(fixture.Root, "tag", "-f", "v6.0.0");
        fixture.Head = (await RunAsync("git", ["rev-parse", "HEAD"], fixture.Root, EmptyEnvironment())).Output.Trim();
        var missingWorkflow = await RunContextAsync(fixture);
        Assert.AreEqual(1, missingWorkflow.ExitCode, missingWorkflow.Combined);

        Directory.CreateDirectory(Path.Combine(fixture.Root, ".github", "workflows"));
        File.Copy(Path.Combine(RepositoryRoot(), WorkflowPath.Replace('/', Path.DirectorySeparatorChar)),
            Path.Combine(fixture.Root, WorkflowPath.Replace('/', Path.DirectorySeparatorChar)), true);
        await GitAsync(fixture.Root, "add", "--all");
        await GitAsync(fixture.Root, "commit", "--quiet", "-m", "restore workflow outside main");
        await GitAsync(fixture.Root, "tag", "-f", "v6.0.0");
        fixture.Head = (await RunAsync("git", ["rev-parse", "HEAD"], fixture.Root, EmptyEnvironment())).Output.Trim();
        var unrelated = await RunContextAsync(fixture);
        Assert.AreEqual(1, unrelated.ExitCode, unrelated.Combined);
        StringAssert.Contains(unrelated.Combined, "origin/main");
    }

    [TestMethod]
    public async Task PublicEnvironmentValidatesTheKeypairWithoutLeakingPrivateMaterial()
    {
        await using var fixture = await CreateFixtureAsync();
        var privateKeyPath = Path.Combine(fixture.Container, "public-release-private.key");
        var publicKeyPath = Path.Combine(fixture.Container, "public-release-anchor.txt");
        var generated = await RunAsync("dotnet",
            ["run", "--project", Path.Combine(fixture.Root, "tools", "CodexUsageMonitor.ReleaseTool", "CodexUsageMonitor.ReleaseTool.csproj"),
                "--configuration", "Debug", "--", "generate-keypair", "--private-key-output", privateKeyPath,
                "--public-key-output", publicKeyPath], fixture.Root, EmptyEnvironment());
        Assert.AreEqual(0, generated.ExitCode, generated.Combined);
        var testTrustAnchor = (await File.ReadAllTextAsync(publicKeyPath)).Trim();
        var testPrivateKey = Convert.ToBase64String(await File.ReadAllBytesAsync(privateKeyPath));
        var anchorPath = Path.Combine(fixture.Root, "packaging", "update", "update-trust-anchor.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(anchorPath)!);
        await File.WriteAllTextAsync(anchorPath, testTrustAnchor + "\n", new UTF8Encoding(false));
        await GitAsync(fixture.Root, "add", anchorPath);
        await GitAsync(fixture.Root, "commit", "--quiet", "-m", "add public trust anchor");
        await GitAsync(fixture.Root, "tag", "-f", "v6.0.0");
        await GitAsync(fixture.Root, "push", "--quiet", "--force", "origin", "HEAD:refs/heads/main");
        fixture.Head = (await RunAsync("git", ["rev-parse", "HEAD"], fixture.Root, EmptyEnvironment())).Output.Trim();

        var environment = ContextEnvironment(fixture);
        environment["UPDATE_PRIVATE_KEY_BASE64"] = testPrivateKey;
        var result = await RunScriptAsync(fixture.Root, "eng/assert-public-release-environment.ps1", environment);

        Assert.AreEqual(0, result.ExitCode, result.Combined);
        Assert.IsFalse(result.Combined.Contains(testPrivateKey, StringComparison.Ordinal));

        environment["UPDATE_PRIVATE_KEY_BASE64"] = null;
        var missing = await RunScriptAsync(fixture.Root, "eng/assert-public-release-environment.ps1", environment);
        Assert.AreEqual(1, missing.ExitCode, missing.Combined);
    }

    private static async Task<Fixture> CreateFixtureAsync()
    {
        var container = Path.Combine(Path.GetTempPath(), "CodexUsageMonitor.PublicReleaseTests", Guid.NewGuid().ToString("N"));
        var root = Path.Combine(container, "worktree");
        var origin = Path.Combine(container, "origin.git");
        Directory.CreateDirectory(container);
        var clone = await RunAsync("git", ["clone", "--quiet", "--shared", "--no-tags", RepositoryRoot(), root], RepositoryRoot(), EmptyEnvironment());
        Assert.AreEqual(0, clone.ExitCode, clone.Combined);

        foreach (var relative in new[]
        {
            "eng/assert-public-release-context.ps1",
            "eng/assert-public-release-environment.ps1",
            WorkflowPath,
        })
        {
            var destination = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(RepositoryRoot(), relative.Replace('/', Path.DirectorySeparatorChar)), destination, true);
        }
        CopyDirectory(Path.Combine(RepositoryRoot(), "tools", "CodexUsageMonitor.ReleaseTool"),
            Path.Combine(root, "tools", "CodexUsageMonitor.ReleaseTool"));

        await GitAsync(root, "config", "user.email", "public-release-tests@example.invalid");
        await GitAsync(root, "config", "user.name", "Public Release Tests");
        await GitAsync(root, "add", "--all");
        await GitAsync(root, "commit", "--quiet", "-m", "public release fixture");
        await GitAsync(root, "tag", "v6.0.0");
        await GitAsync(root, "init", "--quiet", "--bare", origin);
        await GitAsync(root, "remote", "set-url", "origin", origin);
        await GitAsync(root, "push", "--quiet", "origin", "HEAD:refs/heads/main");
        await GitAsync(root, "fetch", "--quiet", "origin", "main");
        var head = (await RunAsync("git", ["rev-parse", "HEAD"], root, EmptyEnvironment())).Output.Trim();
        return new Fixture(root, container, head);
    }

    private static Dictionary<string, string?> ContextEnvironment(Fixture fixture) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["GITHUB_REPOSITORY"] = "saroo98/codex-usage-monitor",
        ["GITHUB_REF"] = "refs/tags/v6.0.0",
        ["GITHUB_REF_TYPE"] = "tag",
        ["GITHUB_REF_NAME"] = "v6.0.0",
        ["GITHUB_RUN_ATTEMPT"] = "1",
        ["GITHUB_WORKFLOW_REF"] = $"saroo98/codex-usage-monitor/{WorkflowPath}@refs/tags/v6.0.0",
        ["GITHUB_WORKFLOW_SHA"] = fixture.Head,
    };

    private static Task<Result> RunContextAsync(Fixture fixture) =>
        RunScriptAsync(fixture.Root, "eng/assert-public-release-context.ps1", ContextEnvironment(fixture));

    private static Task<Result> RunScriptAsync(string root, string script, IReadOnlyDictionary<string, string?> environment) =>
        RunAsync("pwsh", ["-NoProfile", "-File", Path.Combine(root, script.Replace('/', Path.DirectorySeparatorChar)),
            "-Version", Version, "-ExpectedWorkflowPath", WorkflowPath], root, environment);

    private static async Task GitAsync(string root, params string[] arguments)
    {
        var result = await RunAsync("git", arguments, root, EmptyEnvironment());
        Assert.AreEqual(0, result.ExitCode, result.Combined);
    }

    private static async Task<Result> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string?> environment)
    {
        var startInfo = new ProcessStartInfo(fileName) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) { startInfo.ArgumentList.Add(argument); }
        foreach (var pair in environment) { if (pair.Value is null) startInfo.Environment.Remove(pair.Key); else startInfo.Environment[pair.Key] = pair.Value; }
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new Result(process.ExitCode, output, error);
    }

    private static IReadOnlyDictionary<string, string?> EmptyEnvironment() =>
        new Dictionary<string, string?>();

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Directory.Build.props"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class Fixture(string root, string container, string head) : IAsyncDisposable
    {
        public string Root { get; } = root;
        public string Container { get; } = container;
        public string Head { get; set; } = head;
        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Container))
            {
                foreach (var file in Directory.EnumerateFiles(Container, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(Container, true);
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed record Result(int ExitCode, string Output, string Error)
    {
        public string Combined => Output + Error;
    }
}
