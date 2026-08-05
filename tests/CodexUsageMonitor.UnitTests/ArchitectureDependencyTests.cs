using System.Xml.Linq;

namespace CodexUsageMonitor.UnitTests;

[TestClass]
public sealed class ArchitectureDependencyTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [TestMethod]
    public void CoreHasNoProjectOrPackageDependencies()
    {
        var project = LoadProject("src/CodexUsageMonitor.Core/CodexUsageMonitor.Core.csproj");

        Assert.AreEqual(0, References(project, "ProjectReference").Count);
        Assert.AreEqual(0, References(project, "PackageReference").Count);
    }

    [TestMethod]
    public void ApplicationDependsOnlyOnCore()
    {
        var project = LoadProject("src/CodexUsageMonitor.Application/CodexUsageMonitor.Application.csproj");
        var references = References(project, "ProjectReference");

        CollectionAssert.AreEqual(
            new[] { "CodexUsageMonitor.Core.csproj" },
            references.Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
        Assert.AreEqual(0, References(project, "PackageReference").Count);
    }

    [TestMethod]
    public void ApplicationRemainsPlatformNeutral()
    {
        var project = LoadProject("src/CodexUsageMonitor.Application/CodexUsageMonitor.Application.csproj");

        Assert.AreEqual("net10.0", Property(project, "TargetFramework"));
        Assert.IsFalse(string.Equals("true", Property(project, "UseWPF"), StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(string.Equals("true", Property(project, "UseWindowsForms"), StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void MonitoringAdaptersReferenceApplicationModule()
    {
        foreach (var relativePath in new[]
        {
            "src/CodexUsageMonitor.Codex/CodexUsageMonitor.Codex.csproj",
            "src/CodexUsageMonitor.Persistence/CodexUsageMonitor.Persistence.csproj",
            "src/CodexUsageMonitor.Notifications/CodexUsageMonitor.Notifications.csproj",
        })
        {
            var references = References(LoadProject(relativePath), "ProjectReference");
            Assert.IsTrue(
                references.Any(reference => reference.EndsWith("CodexUsageMonitor.Application.csproj", StringComparison.OrdinalIgnoreCase)),
                $"{relativePath} must reference the Application module for its adapter seam.");
        }
    }

    [TestMethod]
    public void WpfShellIsNotReferencedByAnotherProject()
    {
        var offenders = ProjectFiles()
            .Where(path => !path.EndsWith("CodexUsageMonitor.App.csproj", StringComparison.OrdinalIgnoreCase))
            .Where(path => References(XDocument.Load(path), "ProjectReference")
                .Any(reference => reference.EndsWith("CodexUsageMonitor.App.csproj", StringComparison.OrdinalIgnoreCase)))
            .Select(RelativePath)
            .ToArray();

        CollectionAssert.AreEqual(Array.Empty<string>(), offenders);
    }

    [TestMethod]
    public void UpdaterHostDependsOnlyOnUpdater()
    {
        var project = LoadProject("src/CodexUsageMonitor.UpdaterHost/CodexUsageMonitor.UpdaterHost.csproj");
        var references = References(project, "ProjectReference");

        CollectionAssert.AreEqual(
            new[] { "CodexUsageMonitor.Updater.csproj" },
            references.Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
    }

    [TestMethod]
    public void SolutionIncludesApplicationModule()
    {
        var solution = XDocument.Load(Path.Combine(RepositoryRoot, "CodexUsageMonitor.slnx"));
        var paths = solution.Descendants("Project")
            .Select(element => element.Attribute("Path")?.Value.Replace('\\', '/'))
            .Where(static path => path is not null)
            .ToArray();

        CollectionAssert.Contains(paths, "src/CodexUsageMonitor.Application/CodexUsageMonitor.Application.csproj");
    }

    private static XDocument LoadProject(string relativePath) =>
        XDocument.Load(Path.Combine(RepositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static IReadOnlyList<string> References(XDocument project, string itemName) =>
        project.Descendants(itemName)
            .Select(element => element.Attribute("Include")?.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();

    private static string? Property(XDocument project, string propertyName) =>
        project.Descendants(propertyName).Select(static element => element.Value).FirstOrDefault();

    private static IEnumerable<string> ProjectFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "src"), "*.csproj", SearchOption.AllDirectories);

    private static string RelativePath(string path) =>
        Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/');

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CodexUsageMonitor.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the test output directory.");
    }
}
