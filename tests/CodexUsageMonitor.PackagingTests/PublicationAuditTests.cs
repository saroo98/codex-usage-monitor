using System.Diagnostics;
using System.Text;

namespace CodexUsageMonitor.PackagingTests;

[TestClass]
public sealed class PublicationAuditTests
{
    [TestMethod]
    public async Task PublicationAuditRejectsPrivateInternalAndGeneratedRepositoryMaterial()
    {
        var cases = new (string Path, string Content)[]
        {
            ("AGENTS.md", "local agent instructions"),
            ("skills/SKILL.md", "local skill"),
            ("docs/task-7-report.md", "private evidence"),
            ("Codex_Usage_Monitor_Logo_Package/source.txt", "private logo source"),
            ("artifacts/app.exe", "MZ"),
            ("release/private.key", "secret"),
            ("workflow.txt", "secrets." + "SIGN" + "PATH_API_TOKEN"),
            ("secret.txt", "UPDATE_PRIVATE_KEY_BASE64" + "=" + new string('A', 43) + "="),
        };

        foreach (var testCase in cases)
        {
            using var fixture = await CreateFixtureAsync(testCase.Path, testCase.Content);
            var result = await RunAsync("python", ["eng/audit-publication.py"], fixture.Root);
            Assert.AreNotEqual(0, result.ExitCode, $"Publication audit accepted {testCase.Path}. {result.Combined}");
        }
    }

    [TestMethod]
    public async Task PublicationAuditAllowsThePublicTrustAnchorAndPolicyStatements()
    {
        using var fixture = await CreateFixtureAsync(
            "packaging/update/update-trust-anchor.txt",
            "11qYAYKxCrfVS/7TyWQHOg7hcvPapiMlrwIaaPcHURo=\n");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "README.md"),
            "The release does not use an external Windows signing provider.\n", new UTF8Encoding(false));
        var result = await RunAsync("python", ["eng/audit-publication.py"], fixture.Root);
        Assert.AreEqual(0, result.ExitCode, result.Combined);
    }

    private static async Task<Fixture> CreateFixtureAsync(string relativePath, string content)
    {
        var container = Path.Combine(Path.GetTempPath(), "CodexUsageMonitor.PublicationAuditTests", Guid.NewGuid().ToString("N"));
        var root = Path.Combine(container, "repo");
        Directory.CreateDirectory(Path.Combine(root, "eng"));
        File.Copy(Path.Combine(RepositoryRoot(), "eng", "audit-publication.py"), Path.Combine(root, "eng", "audit-publication.py"));
        var target = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, content, new UTF8Encoding(false));
        var init = await RunAsync("git", ["init", "--quiet"], root);
        Assert.AreEqual(0, init.ExitCode, init.Combined);
        return new Fixture(root, container);
    }

    private static async Task<Result> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new Result(process.ExitCode, output, error);
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Directory.Build.props"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class Fixture(string root, string container) : IDisposable
    {
        public string Root { get; } = root;
        public void Dispose()
        {
            if (!Directory.Exists(container)) return;
            foreach (var file in Directory.EnumerateFiles(container, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(container, true);
        }
    }

    private sealed record Result(int ExitCode, string Output, string Error)
    {
        public string Combined => Output + Error;
    }
}
