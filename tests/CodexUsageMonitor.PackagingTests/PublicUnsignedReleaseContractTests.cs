using System.Text.RegularExpressions;

namespace CodexUsageMonitor.PackagingTests;

[TestClass]
public sealed class PublicUnsignedReleaseContractTests
{
    private const string WorkflowName = "native-public-release.yml";

    private static readonly string[] ExactPublicAssets =
    [
        "BUILD-METADATA.json",
        "CodexUsageMonitor-6.0.0-source.zip",
        "CodexUsageMonitor-6.0.0-win-arm64-portable-framework-dependent.zip",
        "CodexUsageMonitor-6.0.0-win-arm64-portable-self-contained.zip",
        "CodexUsageMonitor-6.0.0-win-arm64-update.zip",
        "CodexUsageMonitor-6.0.0-win-x64-portable-framework-dependent.zip",
        "CodexUsageMonitor-6.0.0-win-x64-portable-self-contained.zip",
        "CodexUsageMonitor-6.0.0-win-x64-update.zip",
        "LICENSE.txt",
        "SHA256SUMS.txt",
        "THIRD-PARTY-NOTICES.md",
        "UNSIGNED-WINDOWS-RELEASE.txt",
        "bom.json",
        "update-manifest.json",
    ];

    [TestMethod]
    public void PublicWorkflowPublishesOnlyTheReviewedUnsignedAssetMatrix()
    {
        var workflow = ReadWorkflow();

        foreach (var asset in ExactPublicAssets)
        {
            StringAssert.Contains(workflow, asset, $"The public release workflow must bind the reviewed asset {asset}.");
        }

        foreach (var forbidden in new[]
        {
            "SignPath", "SIGNPATH_", ".pfx", "SIGNING_CERTIFICATE", "Microsoft Store",
            "cosign", ".appinstaller", ".msixbundle", ".msix",
        })
        {
            Assert.IsFalse(workflow.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                $"The public release workflow must not publish or require {forbidden}.");
        }
    }

    [TestMethod]
    public void PublicWorkflowUsesReviewedDispatchSecurityAndPublicationLifecycle()
    {
        var workflow = ReadWorkflow();

        AssertContainsAll(workflow,
            "workflow_dispatch:",
            "version:",
            "default: 6.0.0",
            "publish_confirmed:",
            "type: boolean",
            "default: false",
            "environment: native-production-release",
            "actions: read",
            "contents: read",
            "contents: write",
            "attestations: write",
            "id-token: write",
            "assert-public-release-context.ps1",
            "assert-public-release-environment.ps1",
            "--draft",
            "gh attestation verify",
            "immutable-releases");

        StringAssert.Matches(workflow, new Regex(@"(?m)^\s*uses:\s*actions/attest@[0-9a-f]{40}\s*$"));
        StringAssert.Matches(workflow, new Regex(@"(?m)^\s*uses:\s*actions/checkout@[0-9a-f]{40}\s*$"));
        StringAssert.Matches(workflow, new Regex(@"(?m)^\s*uses:\s*actions/setup-dotnet@[0-9a-f]{40}\s*$"));
        StringAssert.Matches(workflow, new Regex(@"(?m)^\s*uses:\s*actions/setup-python@[0-9a-f]{40}\s*$"));
        Assert.AreEqual(1, Regex.Count(workflow, @"(?m)^\s*environment:\s*native-production-release\s*$"),
            "Only the build-and-package job may use the update-key environment.");
        Assert.IsTrue(Regex.IsMatch(workflow, @"(?is)publish_confirmed.{0,800}(?:throw|exit\s+[1-9]).{0,2500}--draft=false"),
            "Publication must be guarded by the explicit publish_confirmed input.");
    }

    [TestMethod]
    public void PublicWorkflowRemovesLocalVerificationEvidenceBeforeAttestationAndDraftCreation()
    {
        var workflow = ReadWorkflow().Replace("\r\n", "\n", StringComparison.Ordinal);
        const string verification = "./eng/verify-release.ps1 -ReleaseRoot $root";
        const string removal = "Remove-Item -LiteralPath (Join-Path $root 'verification-report.json') -Force";
        const string attestation = "- name: Attest provenance for all public assets";
        const string draftCreation = "gh release create $tag";

        var verificationIndex = workflow.IndexOf(verification, StringComparison.Ordinal);
        var removalIndex = workflow.IndexOf(removal, StringComparison.Ordinal);
        var attestationIndex = workflow.IndexOf(attestation, StringComparison.Ordinal);
        var draftCreationIndex = workflow.IndexOf(draftCreation, StringComparison.Ordinal);

        Assert.IsTrue(verificationIndex >= 0, "The staged release must be independently verified.");
        Assert.IsTrue(removalIndex > verificationIndex,
            "Local verification evidence must be removed only after verification succeeds.");
        Assert.IsTrue(attestationIndex > removalIndex,
            "Local verification evidence must be removed before the public asset attestation.");
        Assert.IsTrue(draftCreationIndex > removalIndex,
            "Local verification evidence must be removed before draft asset upload.");
    }

    [TestMethod]
    public void PublicDocumentationStatesTheUnsignedPortableReleaseTruthfully()
    {
        var combined = string.Join('\n',
            Read("README.md"),
            Read("RELEASE_INTEGRITY.md"),
            Read("RELEASING.md"),
            Read("SECURITY.md"),
            Read("packaging/msix/README.md"),
            Read("docs/index.html"),
            Read("docs/code-signing.html"));

        AssertContainsAll(combined,
            "Windows will show an unverified or unknown publisher because these files are not Authenticode-signed.",
            "Do not disable Windows security controls.",
            "The x64 self-contained portable ZIP is the recommended download for most Windows PCs.",
            "Extract the complete folder before starting CodexUsageMonitor.exe.",
            "GitHub attestations prove build provenance; they do not make an unsigned Windows publisher trusted.");

        foreach (var forbiddenClaim in new[]
        {
            "SignPath is required",
            "MSIX is the recommended download",
            "AppInstaller is the recommended download",
            "external certificate application is pending",
        })
        {
            Assert.IsFalse(combined.Contains(forbiddenClaim, StringComparison.OrdinalIgnoreCase),
                $"Public documentation must not contain the obsolete claim: {forbiddenClaim}");
        }
    }

    private static void AssertContainsAll(string value, params string[] required)
    {
        foreach (var text in required)
        {
            StringAssert.Contains(value, text);
        }
    }

    private static string ReadWorkflow() => Read($".github/workflows/{WorkflowName}");

    private static string Read(string relativePath)
    {
        var path = Path.Combine(RepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.IsTrue(File.Exists(path), $"Required file is missing: {relativePath}");
        return File.ReadAllText(path);
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
}
