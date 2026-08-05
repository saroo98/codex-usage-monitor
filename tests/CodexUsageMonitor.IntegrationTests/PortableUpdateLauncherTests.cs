using System.Security.Cryptography;
using CodexUsageMonitor.Updater.Install;
using CodexUsageMonitor.Updater.Security;
using CodexUsageMonitor.Updater.Staging;

namespace CodexUsageMonitor.IntegrationTests;

[TestClass]
public sealed class PortableUpdateLauncherTests
{
    [TestMethod]
    public async Task LaunchCopiesAndReverifiesHostBeforeStartingBoundedRequest()
    {
        using var fixture = await PortableUpdateTestFixture.CreateAsync();
        Directory.Delete(Path.GetDirectoryName(fixture.Request.UpdaterHostPath)!, recursive: true);
        var verifier = new RecordingSignatureVerifier(isTrusted: true);
        var starter = new RecordingUpdaterHostStarter();
        var launcher = new PortableUpdateLauncher(
            new UpdateArtifactTrustPolicy(verifier, UpdateTrustPolicyOptions.Production),
            starter);

        await launcher.LaunchAsync(fixture.Request, fixture.StagedHost, CancellationToken.None);

        Assert.AreEqual(2, verifier.Paths.Count);
        Assert.AreEqual(Path.GetFullPath(fixture.StagedHost), verifier.Paths[0]);
        Assert.AreEqual(fixture.Request.UpdaterHostPath, verifier.Paths[1]);
        Assert.AreEqual(1, starter.Starts.Count);
        var start = starter.Starts[0];
        Assert.AreEqual(fixture.Request.UpdaterHostPath, start.HostPath);
        Assert.AreEqual("--request", start.RequestOption);
        Assert.AreEqual(fixture.Request.Nonce, start.Nonce);
        Assert.IsTrue(File.Exists(start.RequestPath));
        var journal = await fixture.ReadJournalAsync();
        Assert.AreEqual(UpdateTransactionState.Prepared, journal.State);
        Assert.AreEqual(fixture.Request.TargetApplicationSha256, journal.TargetApplicationSha256);
    }

    [TestMethod]
    public async Task LaunchRejectsUntrustedHostWithoutStartingOrLeavingTransactionDirectory()
    {
        using var fixture = await PortableUpdateTestFixture.CreateAsync();
        Directory.Delete(Path.GetDirectoryName(fixture.Request.UpdaterHostPath)!, recursive: true);
        var starter = new RecordingUpdaterHostStarter();
        var launcher = new PortableUpdateLauncher(
            new UpdateArtifactTrustPolicy(
                new RecordingSignatureVerifier(isTrusted: false),
                UpdateTrustPolicyOptions.Production),
            starter);

        await Assert.ThrowsExactlyAsync<CryptographicException>(() =>
            launcher.LaunchAsync(fixture.Request, fixture.StagedHost, CancellationToken.None));

        Assert.AreEqual(0, starter.Starts.Count);
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(fixture.Request.UpdaterHostPath)!));
    }


    [TestMethod]
    public async Task HostCopyFailureDoesNotStartHostOrLeavePartialTransaction()
    {
        using var fixture = await PortableUpdateTestFixture.CreateAsync();
        var hostDirectory = Path.GetDirectoryName(fixture.Request.UpdaterHostPath)!;
        Directory.Delete(hostDirectory, recursive: true);
        var starter = new RecordingUpdaterHostStarter();
        var launcher = new PortableUpdateLauncher(
            new UpdateArtifactTrustPolicy(
                new RecordingSignatureVerifier(isTrusted: true),
                UpdateTrustPolicyOptions.Production),
            starter,
            new ThrowingHostFileCopier());

        await Assert.ThrowsExactlyAsync<IOException>(() =>
            launcher.LaunchAsync(fixture.Request, fixture.StagedHost, CancellationToken.None));

        Assert.AreEqual(0, starter.Starts.Count);
        Assert.IsFalse(Directory.Exists(hostDirectory));
        Assert.IsFalse(File.Exists(fixture.JournalPath));
    }

    private sealed class RecordingSignatureVerifier(bool isTrusted) : IExecutableSignatureVerifier
    {
        public List<string> Paths { get; } = [];

        public Task<ExecutableSignatureResult> VerifyAsync(
            string filePath,
            IReadOnlySet<string> allowedPublisherThumbprints,
            CancellationToken cancellationToken)
        {
            Paths.Add(Path.GetFullPath(filePath));
            return Task.FromResult(new ExecutableSignatureResult(
                isTrusted,
                isTrusted ? "CN=Test" : null,
                isTrusted ? allowedPublisherThumbprints.Single() : null,
                isTrusted ? null : "signature.publisher_not_allowed"));
        }
    }


    private sealed class ThrowingHostFileCopier : IUpdaterHostFileCopier
    {
        public Task CopyAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken cancellationToken) =>
            throw new IOException("Injected updater host copy failure.");
    }

    private sealed class RecordingUpdaterHostStarter : IUpdaterHostStarter
    {
        public List<StartRecord> Starts { get; } = [];

        public void Start(string hostPath, string requestOption, string requestPath, string nonce) =>
            Starts.Add(new StartRecord(hostPath, requestOption, requestPath, nonce));
    }

    private sealed record StartRecord(string HostPath, string RequestOption, string RequestPath, string Nonce);
}
