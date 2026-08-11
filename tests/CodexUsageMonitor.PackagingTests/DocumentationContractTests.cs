namespace CodexUsageMonitor.PackagingTests;

[TestClass]
public sealed class DocumentationContractTests
{
    [TestMethod]
    public void ReleaseIntegrityExplainsEachIndependentTrustBoundary()
    {
        var policy = Read("RELEASE_INTEGRITY.md");
        AssertContainsAll(policy,
            "# Release integrity",
            "not Authenticode-signed",
            "deterministic portable ZIPs",
            "SHA256SUMS.txt",
            "GitHub artifact attestations",
            "CycloneDX",
            "Ed25519",
            "initial download trust boundary",
            "official GitHub repository",
            "do not make an unsigned Windows publisher trusted",
            "Incident response");
    }

    [TestMethod]
    public void ReleasingGuideUsesOnlyTheGitHubNativeAuthorizationGatedFlow()
    {
        var guide = Read("RELEASING.md");
        AssertContainsAll(guide,
            "git tag v6.0.0",
            "git push origin v6.0.0",
            "gh workflow run native-public-release.yml --ref v6.0.0 -f version=6.0.0 -f publish_confirmed=false",
            "gh workflow run native-public-release.yml --ref v6.0.0 -f version=6.0.0 -f publish_confirmed=true",
            "explicit authorization",
            "UPDATE_PRIVATE_KEY_BASE64",
            "v5.0.0",
            "compromised draft",
            "attestation");
        AssertNoObsoleteProductionSigning(guide);
    }

    [TestMethod]
    public void OrdinaryUserInstallGuidanceRecommendsThePortableX64Package()
    {
        var readme = Read("README.md");
        AssertContainsAll(readme,
            "The x64 self-contained portable ZIP is the recommended download for most Windows PCs.",
            "Extract All",
            "%LOCALAPPDATA%\\Programs\\CodexUsageMonitor",
            "Extract the complete folder before starting CodexUsageMonitor.exe.",
            "Do not disable Windows security controls.",
            "exit the app from the notification area and delete the extracted folder",
            "GitHub Releases page");
        AssertNoObsoleteProductionSigning(readme);
    }

    [TestMethod]
    public void WebsiteAndMsixGuideDescribeTheReviewedPublicAssetHierarchy()
    {
        var home = Read("docs/index.html");
        var integrity = Read("docs/code-signing.html");
        var msix = Read("packaging/msix/README.md");
        AssertContainsAll(home,
            "x64 self-contained portable ZIP",
            "recommended",
            "Arm64",
            "framework-dependent",
            "Windows can show an unverified or unknown publisher",
            "https://github.com/saroo98/codex-usage-monitor/releases");
        AssertContainsAll(integrity,
            "GitHub artifact attestations",
            "SHA256SUMS.txt",
            "CycloneDX",
            "not Authenticode-signed");
        AssertContainsAll(msix,
            "local packaging validation capability only",
            "No unsigned MSIX, bundle, or AppInstaller is a public release asset");
        AssertNoObsoleteProductionSigning(string.Join('\n', home, integrity, msix));
    }

    [TestMethod]
    public void SecurityPolicyLinksReleaseIntegrityAndIncidentProcedure()
    {
        var security = Read("SECURITY.md");
        AssertContainsAll(security,
            "[release integrity policy](RELEASE_INTEGRITY.md)",
            "[release incident procedure](RELEASING.md#incident-response-and-rollback)");
    }

    private static void AssertNoObsoleteProductionSigning(string text)
    {
        foreach (var obsolete in new[] { "SignPath", "SIGNPATH_", "PFX", "Microsoft Store enrollment", "Cosign" })
        {
            Assert.IsFalse(text.Contains(obsolete, StringComparison.OrdinalIgnoreCase),
                $"Public release documentation must not depend on {obsolete}.");
        }
    }

    private static void AssertContainsAll(string value, params string[] required)
    {
        foreach (var text in required) StringAssert.Contains(value, text);
    }

    private static string Read(string relativePath)
    {
        var path = Path.Combine(RepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.IsTrue(File.Exists(path), $"Required public documentation file is missing: {relativePath}");
        return File.ReadAllText(path);
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Directory.Build.props"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
