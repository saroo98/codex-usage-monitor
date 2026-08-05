using System.Reflection;
using CodexUsageMonitor.Updater.Install;
using CodexUsageMonitor.Updater.Security;

namespace CodexUsageMonitor.Updater.Staging;

public enum UpdateArtifactTrustMode
{
    PublisherSignature,
    DevelopmentFileManifest,
}

public sealed record UpdateTrustPolicyOptions(bool AllowUnsignedDevelopmentArtifacts)
{
    public const string AllowUnsignedDevelopmentArtifactsEnvironmentVariable =
        "CODEX_USAGE_MONITOR_ALLOW_UNSIGNED_DEVELOPMENT_UPDATES";

    public static UpdateTrustPolicyOptions Production { get; } = new(false);

    public static UpdateTrustPolicyOptions FromEnvironment() => new(
        string.Equals(
            Environment.GetEnvironmentVariable(AllowUnsignedDevelopmentArtifactsEnvironmentVariable),
            "1",
            StringComparison.Ordinal));
}

public sealed class UpdateArtifactTrustPolicy
{
    private readonly IExecutableSignatureVerifier _signatureVerifier;
    private readonly UpdateTrustPolicyOptions _options;

    public UpdateArtifactTrustPolicy(
        IExecutableSignatureVerifier signatureVerifier,
        UpdateTrustPolicyOptions options)
    {
        _signatureVerifier = signatureVerifier ?? throw new ArgumentNullException(nameof(signatureVerifier));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<UpdateArtifactTrustMode> VerifyStagedExecutablesAsync(
        string applicationPath,
        string updaterHostPath,
        IReadOnlyList<string> publisherThumbprints,
        VerifiedUpdatePackageManifest packageManifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packageManifest);
        if (publisherThumbprints.Count > 0)
        {
            var pins = UpdatePublisherPins.ToSet(publisherThumbprints);
            var applicationResult = await _signatureVerifier.VerifyAsync(
                applicationPath,
                pins,
                cancellationToken).ConfigureAwait(false);
            if (!applicationResult.IsTrusted)
            {
                throw new System.Security.Cryptography.CryptographicException(
                    applicationResult.SafeErrorCode ?? "The update application publisher is not trusted.");
            }

            var updaterResult = await _signatureVerifier.VerifyAsync(
                updaterHostPath,
                pins,
                cancellationToken).ConfigureAwait(false);
            if (!updaterResult.IsTrusted)
            {
                throw new System.Security.Cryptography.CryptographicException(
                    updaterResult.SafeErrorCode ?? "The updater host publisher is not trusted.");
            }

            return UpdateArtifactTrustMode.PublisherSignature;
        }

        EnsureDevelopmentFallbackAllowed();
        var applicationEntry = packageManifest.GetRequiredEntry(UpdatePathLayout.ApplicationExecutableName);
        var hostEntry = packageManifest.GetRequiredEntry(UpdatePathLayout.UpdaterHostExecutableName);
        await UpdateFileIntegrity.VerifySha256Async(
            applicationPath,
            applicationEntry.Sha256,
            "The unsigned development application does not match its build-generated manifest.",
            cancellationToken).ConfigureAwait(false);
        await UpdateFileIntegrity.VerifySha256Async(
            updaterHostPath,
            hostEntry.Sha256,
            "The unsigned development updater host does not match its build-generated manifest.",
            cancellationToken).ConfigureAwait(false);
        return UpdateArtifactTrustMode.DevelopmentFileManifest;
    }

    public async Task VerifyPreparedHostAsync(
        string hostPath,
        string expectedSha256,
        IReadOnlyList<string> publisherThumbprints,
        UpdateArtifactTrustMode trustMode,
        CancellationToken cancellationToken)
    {
        await UpdateFileIntegrity.VerifySha256Async(
            hostPath,
            expectedSha256,
            "The updater host failed integrity verification.",
            cancellationToken).ConfigureAwait(false);
        switch (trustMode)
        {
            case UpdateArtifactTrustMode.PublisherSignature:
                var result = await _signatureVerifier.VerifyAsync(
                    hostPath,
                    UpdatePublisherPins.ToSet(publisherThumbprints),
                    cancellationToken).ConfigureAwait(false);
                if (!result.IsTrusted)
                {
                    throw new System.Security.Cryptography.CryptographicException(
                        result.SafeErrorCode ?? "The updater host publisher is not trusted.");
                }

                break;
            case UpdateArtifactTrustMode.DevelopmentFileManifest:
                EnsureDevelopmentFallbackAllowed();
                break;
            default:
                throw new InvalidDataException("The updater trust mode is invalid.");
        }
    }

    private void EnsureDevelopmentFallbackAllowed()
    {
        if (!_options.AllowUnsignedDevelopmentArtifacts || !UpdateBuildIdentity.IsDevelopmentBuild)
        {
            throw new System.Security.Cryptography.CryptographicException(
                "Unsigned updater artifacts are not permitted by this build and runtime configuration.");
        }
    }
}

internal static class UpdateBuildIdentity
{
    private const string BuildFlavorMetadataName = "UpdateBuildFlavor";

    public static bool IsDevelopmentBuild
    {
        get
        {
            var flavor = typeof(UpdateBuildIdentity).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .SingleOrDefault(attribute => string.Equals(
                    attribute.Key,
                    BuildFlavorMetadataName,
                    StringComparison.Ordinal))
                ?.Value;
            return string.Equals(flavor, "Development", StringComparison.Ordinal);
        }
    }
}
