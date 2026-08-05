using CodexUsageMonitor.Updater.Install;
using CodexUsageMonitor.Updater.Staging;

namespace CodexUsageMonitor.IntegrationTests;

[TestClass]
public sealed class UpdatePackageFileManifestTests
{
    [TestMethod]
    public async Task ValidManifestCoversEveryExtractedFile()
    {
        using var fixture = await PortableUpdateTestFixture.CreateAsync();

        var verified = await UpdatePackageFileManifest.ReadAndVerifyAsync(
            fixture.Request.StagingDirectory,
            fixture.Request.Version,
            CancellationToken.None);

        Assert.AreEqual(fixture.Request.PackageFileManifestSha256, verified.ManifestSha256);
        Assert.AreEqual(
            fixture.Request.TargetApplicationSha256,
            verified.GetRequiredEntry(UpdatePathLayout.ApplicationExecutableName).Sha256);
        Assert.AreEqual(
            fixture.Request.UpdaterHostSha256,
            verified.GetRequiredEntry(UpdatePathLayout.UpdaterHostExecutableName).Sha256);
    }

    [TestMethod]
    public async Task UnlistedFileIsRejected()
    {
        using var fixture = await PortableUpdateTestFixture.CreateAsync();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Request.StagingDirectory, "unlisted.dll"),
            "not-in-manifest");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UpdatePackageFileManifest.ReadAndVerifyAsync(
                fixture.Request.StagingDirectory,
                fixture.Request.Version,
                CancellationToken.None));
    }

    [TestMethod]
    public async Task TamperedDeclaredFileIsRejected()
    {
        using var fixture = await PortableUpdateTestFixture.CreateAsync();
        var payload = await File.ReadAllBytesAsync(fixture.StagedApplication);
        payload[0] ^= 0xff;
        await File.WriteAllBytesAsync(fixture.StagedApplication, payload);

        await Assert.ThrowsExactlyAsync<System.Security.Cryptography.CryptographicException>(() =>
            UpdatePackageFileManifest.ReadAndVerifyAsync(
                fixture.Request.StagingDirectory,
                fixture.Request.Version,
                CancellationToken.None));
    }

    [TestMethod]
    public async Task ReservedPortablePayloadIsRejectedBeforeInstallPreparation()
    {
        using var fixture = await PortableUpdateTestFixture.CreateAsync();
        Directory.CreateDirectory(Path.Combine(
            fixture.Request.StagingDirectory,
            "data"));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UpdatePackageFileManifest.ReadAndVerifyAsync(
                fixture.Request.StagingDirectory,
                fixture.Request.Version,
                CancellationToken.None));
    }

    [TestMethod]
    public async Task PreparedPortablePayloadIsAllowedOnlyInInstalledVerificationMode()
    {
        using var fixture = await PortableUpdateTestFixture.CreateAsync(portableDataMode: true);
        await File.WriteAllBytesAsync(
            Path.Combine(fixture.Request.StagingDirectory, "portable.mode"),
            Array.Empty<byte>());
        var stagedData = Path.Combine(fixture.Request.StagingDirectory, "data");
        Directory.CreateDirectory(stagedData);
        await File.WriteAllTextAsync(Path.Combine(stagedData, "settings.json"), "portable-data");

        var verified = await UpdatePackageFileManifest.ReadAndVerifyAsync(
            fixture.Request.StagingDirectory,
            fixture.Request.Version,
            CancellationToken.None,
            allowPortablePayload: true);

        Assert.AreEqual(fixture.Request.PackageFileManifestSha256, verified.ManifestSha256);
        Assert.IsTrue(File.Exists(Path.Combine(
            fixture.Request.StagingDirectory,
            "portable.mode")));
        Assert.IsTrue(Directory.Exists(Path.Combine(
            fixture.Request.StagingDirectory,
            "data")));
    }
}
