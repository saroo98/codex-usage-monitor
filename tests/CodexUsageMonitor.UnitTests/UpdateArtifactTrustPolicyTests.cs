using System.Security.Cryptography;
using CodexUsageMonitor.Updater.Install;
using CodexUsageMonitor.Updater.Security;
using CodexUsageMonitor.Updater.Staging;

namespace CodexUsageMonitor.UnitTests;

[TestClass]
[DoNotParallelize]
public sealed class UpdateArtifactTrustPolicyTests
{
    private static readonly string PublisherThumbprint = new('A', 40);

    [TestMethod]
    public async Task PublisherModeRequiresBothExecutablesToMatchPinnedPublisher()
    {
        using var fixture = await TrustFixture.CreateAsync();
        var verifier = new RecordingSignatureVerifier(static _ => true);
        var policy = new UpdateArtifactTrustPolicy(verifier, UpdateTrustPolicyOptions.Production);

        var mode = await policy.VerifyStagedExecutablesAsync(
            fixture.ApplicationPath,
            fixture.HostPath,
            [PublisherThumbprint],
            fixture.Manifest,
            CancellationToken.None);

        Assert.AreEqual(UpdateArtifactTrustMode.PublisherSignature, mode);
        CollectionAssert.AreEqual(
            new[] { fixture.ApplicationPath, fixture.HostPath },
            verifier.Paths.ToArray());
    }

    [TestMethod]
    public async Task PublisherModeRejectsOneUntrustedExecutable()
    {
        using var fixture = await TrustFixture.CreateAsync();
        var verifier = new RecordingSignatureVerifier(path =>
            !string.Equals(path, fixture.HostPath, StringComparison.OrdinalIgnoreCase));
        var policy = new UpdateArtifactTrustPolicy(verifier, UpdateTrustPolicyOptions.Production);

        await Assert.ThrowsExactlyAsync<CryptographicException>(() =>
            policy.VerifyStagedExecutablesAsync(
                fixture.ApplicationPath,
                fixture.HostPath,
                [PublisherThumbprint],
                fixture.Manifest,
                CancellationToken.None));
    }


    [TestMethod]
    public async Task PreparedHostHashMismatchIsRejectedBeforeSignatureVerification()
    {
        using var fixture = await TrustFixture.CreateAsync();
        var verifier = new RecordingSignatureVerifier(static _ => true);
        var policy = new UpdateArtifactTrustPolicy(verifier, UpdateTrustPolicyOptions.Production);

        await Assert.ThrowsExactlyAsync<CryptographicException>(() =>
            policy.VerifyPreparedHostAsync(
                fixture.HostPath,
                new string('0', UpdateFileIntegrity.Sha256HexLength),
                [PublisherThumbprint],
                UpdateArtifactTrustMode.PublisherSignature,
                CancellationToken.None));

        Assert.AreEqual(0, verifier.Paths.Count);
    }

    [TestMethod]
    public async Task PreparedHostPublisherModeRejectsUntrustedCopiedHost()
    {
        using var fixture = await TrustFixture.CreateAsync();
        var verifier = new RecordingSignatureVerifier(static _ => false);
        var policy = new UpdateArtifactTrustPolicy(verifier, UpdateTrustPolicyOptions.Production);
        var hostSha256 = await UpdateFileIntegrity.ComputeSha256Async(
            fixture.HostPath,
            CancellationToken.None);

        await Assert.ThrowsExactlyAsync<CryptographicException>(() =>
            policy.VerifyPreparedHostAsync(
                fixture.HostPath,
                hostSha256,
                [PublisherThumbprint],
                UpdateArtifactTrustMode.PublisherSignature,
                CancellationToken.None));

        CollectionAssert.AreEqual(new[] { fixture.HostPath }, verifier.Paths.ToArray());
    }

    [TestMethod]
    public async Task DevelopmentBuildCannotEnableProjectManifestModeAndHashVerificationRunsFirst()
    {
        using var fixture = await TrustFixture.CreateAsync();
        var verifier = new RecordingSignatureVerifier(static _ => false);
        var policy = new UpdateArtifactTrustPolicy(verifier, UpdateTrustPolicyOptions.Production);
        var hostSha256 = await UpdateFileIntegrity.ComputeSha256Async(
            fixture.HostPath,
            CancellationToken.None);
        var projectManifestMode = Enum.Parse<UpdateArtifactTrustMode>("ProjectManifest");

        var buildFailure = await Assert.ThrowsExactlyAsync<CryptographicException>(() =>
            policy.VerifyPreparedHostAsync(
                fixture.HostPath,
                hostSha256,
                [],
                projectManifestMode,
                CancellationToken.None));

        StringAssert.Contains(buildFailure.Message, "not permitted by this build");
        Assert.AreEqual(0, verifier.Paths.Count);

        await File.AppendAllTextAsync(fixture.HostPath, "tampered");
        var integrityFailure = await Assert.ThrowsExactlyAsync<CryptographicException>(() =>
            policy.VerifyPreparedHostAsync(
                fixture.HostPath,
                hostSha256,
                [],
                projectManifestMode,
                CancellationToken.None));
        StringAssert.Contains(integrityFailure.Message, "integrity verification");
    }

    [TestMethod]
    public void DevelopmentEnvironmentOptInRequiresExactOne()
    {
        const string variable = UpdateTrustPolicyOptions.AllowUnsignedDevelopmentArtifactsEnvironmentVariable;
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "1");
            Assert.IsTrue(UpdateTrustPolicyOptions.FromEnvironment().AllowUnsignedDevelopmentArtifacts);

            foreach (var value in new string?[] { null, string.Empty, "true", "TRUE", " 1", "1 ", "0" })
            {
                Environment.SetEnvironmentVariable(variable, value);
                Assert.IsFalse(UpdateTrustPolicyOptions.FromEnvironment().AllowUnsignedDevelopmentArtifacts);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [TestMethod]
    public async Task UnsignedFallbackIsRejectedWithoutExplicitRuntimeOptIn()
    {
        using var fixture = await TrustFixture.CreateAsync();
        var policy = new UpdateArtifactTrustPolicy(
            new RecordingSignatureVerifier(static _ => false),
            UpdateTrustPolicyOptions.Production);

        await Assert.ThrowsExactlyAsync<CryptographicException>(() =>
            policy.VerifyStagedExecutablesAsync(
                fixture.ApplicationPath,
                fixture.HostPath,
                [],
                fixture.Manifest,
                CancellationToken.None));
    }

    [TestMethod]
    [TestCategory("PublicUnsignedBuild")]
    public async Task PublicUnsignedBuildSelectsProjectManifestAndRejectsChangedExecutableBytes()
    {
        var buildFlavor = typeof(UpdateArtifactTrustPolicy).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), inherit: false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .Single(attribute => string.Equals(attribute.Key, "UpdateBuildFlavor", StringComparison.Ordinal))
            .Value;
        if (!string.Equals(buildFlavor, "PublicUnsigned", StringComparison.Ordinal))
        {
            Assert.Inconclusive("This contract runs only when MSBuild sets UpdateBuildFlavor=PublicUnsigned.");
        }

        using var fixture = await TrustFixture.CreateAsync();
        var verifier = new RecordingSignatureVerifier(static _ => false);
        var policy = new UpdateArtifactTrustPolicy(verifier, UpdateTrustPolicyOptions.Production);

        var mode = await policy.VerifyStagedExecutablesAsync(
            fixture.ApplicationPath,
            fixture.HostPath,
            [],
            fixture.Manifest,
            CancellationToken.None);

        Assert.AreEqual(UpdateArtifactTrustMode.ProjectManifest, mode);
        Assert.AreEqual(0, verifier.Paths.Count);

        await File.AppendAllTextAsync(fixture.ApplicationPath, "tampered");
        await Assert.ThrowsExactlyAsync<CryptographicException>(() =>
            policy.VerifyStagedExecutablesAsync(
                fixture.ApplicationPath,
                fixture.HostPath,
                [],
                fixture.Manifest,
                CancellationToken.None));
    }

#if DEBUG
    [TestMethod]
    public async Task DevelopmentFallbackRequiresExactBuildManifestHashes()
    {
        using var fixture = await TrustFixture.CreateAsync();
        var policy = new UpdateArtifactTrustPolicy(
            new RecordingSignatureVerifier(static _ => false),
            new UpdateTrustPolicyOptions(AllowUnsignedDevelopmentArtifacts: true));

        var mode = await policy.VerifyStagedExecutablesAsync(
            fixture.ApplicationPath,
            fixture.HostPath,
            [],
            fixture.Manifest,
            CancellationToken.None);
        Assert.AreEqual(UpdateArtifactTrustMode.DevelopmentFileManifest, mode);

        await File.AppendAllTextAsync(fixture.HostPath, "tampered");
        await Assert.ThrowsExactlyAsync<CryptographicException>(() =>
            policy.VerifyStagedExecutablesAsync(
                fixture.ApplicationPath,
                fixture.HostPath,
                [],
                fixture.Manifest,
                CancellationToken.None));
    }
#endif

    private sealed class RecordingSignatureVerifier(Func<string, bool> isTrusted) : IExecutableSignatureVerifier
    {
        public List<string> Paths { get; } = [];

        public Task<ExecutableSignatureResult> VerifyAsync(
            string filePath,
            IReadOnlySet<string> allowedPublisherThumbprints,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(filePath);
            Paths.Add(fullPath);
            var trusted = isTrusted(fullPath);
            return Task.FromResult(new ExecutableSignatureResult(
                trusted,
                trusted ? "CN=Test Publisher" : null,
                trusted ? PublisherThumbprint : null,
                trusted ? null : "signature.publisher_not_pinned"));
        }
    }

    private sealed class TrustFixture : IDisposable
    {
        private TrustFixture(
            string root,
            string applicationPath,
            string hostPath,
            VerifiedUpdatePackageManifest manifest)
        {
            Root = root;
            ApplicationPath = applicationPath;
            HostPath = hostPath;
            Manifest = manifest;
        }

        public string Root { get; }
        public string ApplicationPath { get; }
        public string HostPath { get; }
        public VerifiedUpdatePackageManifest Manifest { get; }

        public static async Task<TrustFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "cum-update-trust", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var applicationPath = Path.Combine(root, UpdatePathLayout.ApplicationExecutableName);
            var hostPath = Path.Combine(root, UpdatePathLayout.UpdaterHostExecutableName);
            await File.WriteAllTextAsync(applicationPath, "application");
            await File.WriteAllTextAsync(hostPath, "host");
            var entries = new[]
            {
                new UpdatePackageFileEntry(
                    UpdatePathLayout.ApplicationExecutableName,
                    new FileInfo(applicationPath).Length,
                    await UpdateFileIntegrity.ComputeSha256Async(applicationPath, CancellationToken.None)),
                new UpdatePackageFileEntry(
                    UpdatePathLayout.UpdaterHostExecutableName,
                    new FileInfo(hostPath).Length,
                    await UpdateFileIntegrity.ComputeSha256Async(hostPath, CancellationToken.None)),
            }.OrderBy(static entry => entry.Path, StringComparer.Ordinal).ToArray();
            var manifest = new UpdatePackageFileManifest(
                UpdatePackageFileManifest.CurrentSchemaVersion,
                "1.0.1",
                entries);
            var verified = new VerifiedUpdatePackageManifest(
                manifest,
                Path.Combine(root, UpdatePathLayout.PackageFileManifestName),
                new string('b', UpdateFileIntegrity.Sha256HexLength));
            return new TrustFixture(root, applicationPath, hostPath, verified);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
