using CodexUsageMonitor.Updater.Manifest;
using CodexUsageMonitor.Updater.Staging;

namespace CodexUsageMonitor.UnitTests;

[TestClass]
public sealed class UpdateManifestValidatorTests
{
    private static readonly DateTimeOffset PublishedAt =
        new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ProductionManifestRequiresCanonicalVersionArchitecturesPinsAndSignature()
    {
        var manifest = CreateManifest();

        new UpdateManifestValidator(UpdateTrustPolicyOptions.Production).Validate(manifest);
    }

    [TestMethod]
    [DataRow("Stable")]
    [DataRow(" stable")]
    [DataRow("nightly")]
    public void RejectsNonCanonicalOrUnsupportedChannel(string channel)
    {
        var manifest = CreateManifest() with { Channel = channel };

        Assert.ThrowsExactly<InvalidDataException>(() =>
            new UpdateManifestValidator(UpdateTrustPolicyOptions.Production).Validate(manifest));
    }

    [TestMethod]
    [DataRow("v1.2.3")]
    [DataRow("01.2.3")]
    [DataRow("1.2")]
    [DataRow("1.2.3-01")]
    public void RejectsNonCanonicalSemanticVersion(string version)
    {
        var manifest = CreateManifest() with { Version = version };

        Assert.ThrowsExactly<InvalidDataException>(() =>
            new UpdateManifestValidator(UpdateTrustPolicyOptions.Production).Validate(manifest));
    }

    [TestMethod]
    [DataRow("http://updates.example.test/release")]
    [DataRow("https://user:password@updates.example.test/release")]
    [DataRow("https://updates.example.test/release#fragment")]
    [DataRow("not-a-url")]
    public void RejectsUnsafeReleaseNotesUrl(string url)
    {
        var manifest = CreateManifest() with { ReleaseNotesUrl = url };

        Assert.ThrowsExactly<InvalidDataException>(() =>
            new UpdateManifestValidator(UpdateTrustPolicyOptions.Production).Validate(manifest));
    }

    [TestMethod]
    [DataRow("../update.zip")]
    [DataRow("CON.zip")]
    [DataRow("update.zip.")]
    [DataRow("update?.zip")]
    public void RejectsUnsafeWindowsAssetFileName(string fileName)
    {
        var manifest = CreateManifest();
        var assets = manifest.Assets.ToArray();
        assets[0] = assets[0] with { FileName = fileName };

        Assert.ThrowsExactly<InvalidDataException>(() =>
            new UpdateManifestValidator(UpdateTrustPolicyOptions.Production)
                .Validate(manifest with { Assets = assets }));
    }

    [TestMethod]
    public void RejectsCaseVariantArchitectureAndUppercaseDigest()
    {
        var manifest = CreateManifest();
        var assets = manifest.Assets.ToArray();
        assets[0] = assets[0] with
        {
            Architecture = "X64",
            Sha256 = new string('A', 64),
        };

        Assert.ThrowsExactly<InvalidDataException>(() =>
            new UpdateManifestValidator(UpdateTrustPolicyOptions.Production)
                .Validate(manifest with { Assets = assets }));
    }

    [TestMethod]
    public void RejectsMissingProductionPublisherPins()
    {
        var manifest = CreateManifest();
        var assets = manifest.Assets
            .Select(static asset => asset with { PublisherThumbprints = [] })
            .ToArray();

        Assert.ThrowsExactly<InvalidDataException>(() =>
            new UpdateManifestValidator(UpdateTrustPolicyOptions.Production)
                .Validate(manifest with { Assets = assets }));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("not-base64")]
    [DataRow("AA==")]
    public void RejectsInvalidManifestSignature(string signature)
    {
        var manifest = CreateManifest() with { Signature = signature };

        Assert.ThrowsExactly<InvalidDataException>(() =>
            new UpdateManifestValidator(UpdateTrustPolicyOptions.Production).Validate(manifest));
    }

    private static UpdateManifestDocument CreateManifest() => new(
        UpdateManifestDocument.CurrentSchemaVersion,
        "stable",
        "1.2.3",
        PublishedAt,
        19041,
        "https://updates.example.test/releases/1.2.3",
        [
            new UpdateAsset(
                "arm64",
                "https://updates.example.test/1.2.3/arm64.zip",
                "CodexUsageMonitor-1.2.3-win-arm64.zip",
                1024,
                new string('a', 64),
                [new string('A', 40)]),
            new UpdateAsset(
                "x64",
                "https://updates.example.test/1.2.3/x64.zip",
                "CodexUsageMonitor-1.2.3-win-x64.zip",
                2048,
                new string('b', 64),
                [new string('B', 40)]),
        ],
        Convert.ToBase64String(new byte[64]));
}
