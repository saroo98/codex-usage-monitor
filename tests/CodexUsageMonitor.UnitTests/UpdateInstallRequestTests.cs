using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexUsageMonitor.Updater.Install;
using CodexUsageMonitor.Updater.Staging;

namespace CodexUsageMonitor.UnitTests;

[TestClass]
public sealed class UpdateInstallRequestTests
{
    [TestMethod]
    public async Task PayloadVerificationAcceptsPreparedFilesAndRejectsTampering()
    {
        using var fixture = await UpdateRequestFixture.CreateAsync();

        await fixture.Request.VerifyPayloadAsync(CancellationToken.None);
        var payload = await File.ReadAllBytesAsync(fixture.StagedApplicationPath);
        payload[0] ^= 0xff;
        await File.WriteAllBytesAsync(fixture.StagedApplicationPath, payload);

        await Assert.ThrowsExactlyAsync<CryptographicException>(() =>
            fixture.Request.VerifyPayloadAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task ValidationAcceptsExactEnvelopeAndRejectsNonceHostPathAndReplayMismatch()
    {
        using var fixture = await UpdateRequestFixture.CreateAsync();
        await fixture.Request.WriteAsync(fixture.RequestPath, CancellationToken.None);

        fixture.Request.Validate(
            fixture.Request.Nonce,
            fixture.Request.UpdaterHostPath,
            fixture.RequestPath,
            fixture.Now);

        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Request.Validate(
            new string('0', 64),
            fixture.Request.UpdaterHostPath,
            fixture.RequestPath,
            fixture.Now));
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Request.Validate(
            fixture.Request.Nonce,
            Path.Combine(fixture.Request.InstallationDirectory, "untrusted-host.exe"),
            fixture.RequestPath,
            fixture.Now));
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Request.Validate(
            fixture.Request.Nonce,
            fixture.Request.UpdaterHostPath,
            fixture.RequestPath + ".copy",
            fixture.Now));
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Request.Validate(
            fixture.Request.Nonce,
            fixture.Request.UpdaterHostPath,
            fixture.RequestPath,
            fixture.Now.AddMinutes(16)));
    }

    [TestMethod]
    public async Task StructuralValidationRejectsPathEscapeAndPortableModeChange()
    {
        using var fixture = await UpdateRequestFixture.CreateAsync();
        var escaped = fixture.Request with
        {
            StagingDirectory = Path.Combine(fixture.Root, "other-stage"),
        };

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            escaped.WriteAsync(fixture.RequestPath, CancellationToken.None));

        await File.WriteAllBytesAsync(
            Path.Combine(fixture.Request.InstallationDirectory, "portable.mode"),
            []);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            fixture.Request.WriteAsync(fixture.RequestPath, CancellationToken.None));
    }

    [TestMethod]
    public async Task RequestReadRejectsOversizedAndDuplicatePropertyPayloads()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cum-request-size", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var oversizedPath = Path.Combine(directory, "oversized.json");
        var duplicatePath = Path.Combine(directory, "duplicate.json");
        try
        {
            await File.WriteAllBytesAsync(
                oversizedPath,
                new byte[UpdateInstallRequest.MaximumSerializedBytes + 1]);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                UpdateInstallRequest.ReadAsync(oversizedPath, CancellationToken.None));

            await File.WriteAllTextAsync(
                duplicatePath,
                "{\"schemaVersion\":2,\"schemaVersion\":2}",
                Encoding.UTF8);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                UpdateInstallRequest.ReadAsync(duplicatePath, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class UpdateRequestFixture : IDisposable
    {
        private UpdateRequestFixture(
            string root,
            UpdateInstallRequest request,
            string stagedApplicationPath,
            string requestPath,
            DateTimeOffset now)
        {
            Root = root;
            Request = request;
            StagedApplicationPath = stagedApplicationPath;
            RequestPath = requestPath;
            Now = now;
        }

        public string Root { get; }
        public UpdateInstallRequest Request { get; }
        public string StagedApplicationPath { get; }
        public string RequestPath { get; }
        public DateTimeOffset Now { get; }

        public static async Task<UpdateRequestFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "cum-request", Guid.NewGuid().ToString("N"));
            var install = Path.Combine(root, "app");
            var stage = UpdatePathLayout.GetStagingDirectory(install, "1.0.1");
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(stage);
            var installedApplication = Path.Combine(install, UpdatePathLayout.ApplicationExecutableName);
            var stagedApplication = Path.Combine(stage, UpdatePathLayout.ApplicationExecutableName);
            var stagedHost = Path.Combine(stage, UpdatePathLayout.UpdaterHostExecutableName);
            var stagedLibrary = Path.Combine(stage, "runtime-component.dll");
            await File.WriteAllTextAsync(installedApplication, "current");
            await File.WriteAllTextAsync(stagedApplication, "target");
            await File.WriteAllTextAsync(stagedHost, "host");
            await File.WriteAllTextAsync(stagedLibrary, "library");

            var packageManifestHash = await WritePackageManifestAsync(stage);
            var now = DateTimeOffset.UtcNow;
            var request = UpdateInstallRequest.Create(
                "1.0.1",
                "1.0.0",
                Environment.ProcessId,
                now.AddSeconds(-1),
                install,
                stage,
                false,
                await UpdateFileIntegrity.ComputeSha256Async(installedApplication, CancellationToken.None),
                await UpdateFileIntegrity.ComputeSha256Async(stagedApplication, CancellationToken.None),
                await UpdateFileIntegrity.ComputeSha256Async(stagedHost, CancellationToken.None),
                packageManifestHash,
                UpdateArtifactTrustMode.PublisherSignature,
                [new string('A', 40)],
                now);
            Directory.CreateDirectory(Path.GetDirectoryName(request.UpdaterHostPath)!);
            File.Copy(stagedHost, request.UpdaterHostPath);
            var requestPath = UpdatePathLayout.GetInstallRequestPath(
                request.InstallationDirectory,
                request.TransactionId);
            return new UpdateRequestFixture(root, request, stagedApplication, requestPath, now);
        }

        private static async Task<string> WritePackageManifestAsync(string stage)
        {
            var entries = new List<UpdatePackageFileEntry>();
            foreach (var path in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(stage, path).Replace(Path.DirectorySeparatorChar, '/');
                var info = new FileInfo(path);
                entries.Add(new UpdatePackageFileEntry(
                    relative,
                    info.Length,
                    await UpdateFileIntegrity.ComputeSha256Async(path, CancellationToken.None)));
            }

            entries.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));
            var manifest = new UpdatePackageFileManifest(
                UpdatePackageFileManifest.CurrentSchemaVersion,
                "1.0.1",
                entries);
            var manifestPath = Path.Combine(stage, UpdatePathLayout.PackageFileManifestName);
            await using (var stream = new FileStream(
                             manifestPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, manifest);
                await stream.FlushAsync();
            }

            return await UpdateFileIntegrity.ComputeSha256Async(manifestPath, CancellationToken.None);
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
