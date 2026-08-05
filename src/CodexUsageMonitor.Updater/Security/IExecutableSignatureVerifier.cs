namespace CodexUsageMonitor.Updater.Security;

public sealed record ExecutableSignatureResult(
    bool IsTrusted,
    string? PublisherSubject,
    string? CertificateThumbprint,
    string? SafeErrorCode);

public interface IExecutableSignatureVerifier
{
    Task<ExecutableSignatureResult> VerifyAsync(
        string filePath,
        IReadOnlySet<string> allowedPublisherThumbprints,
        CancellationToken cancellationToken);
}
