namespace CodexUsageMonitor.Application.Updates;

public enum UpdateCheckOutcomeStatus
{
    NotModified,
    Current,
    Available,
    UnsupportedOperatingSystem,
    UnsupportedArchitecture,
}

public sealed record UpdateCheckOutcome(
    UpdateCheckOutcomeStatus Status,
    string? AvailableVersion,
    string? ReleaseNotesUrl,
    string? EntityTag,
    bool HasVerifiedAsset);

public interface IUpdatePlatformPort
{
    bool IsManagedExternally { get; }

    bool HasVerifiedCandidate { get; }

    bool HasPreparedUpdate { get; }

    Task<UpdateCheckOutcome> CheckAsync(
        Uri manifestUri,
        string channel,
        string? entityTag,
        CancellationToken cancellationToken);

    Task PrepareAsync(IProgress<double> progress, CancellationToken cancellationToken);

    Task LaunchPreparedAsync(CancellationToken cancellationToken);
}
