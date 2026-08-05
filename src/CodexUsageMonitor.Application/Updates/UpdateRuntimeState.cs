namespace CodexUsageMonitor.Application.Updates;

public enum UpdateRuntimeStatus
{
    NotConfigured,
    Ready,
    Checking,
    Current,
    Available,
    Downloading,
    Staged,
    Installing,
    Recovering,
    ManagedExternally,
    UnsupportedOperatingSystem,
    UnsupportedArchitecture,
    Failed,
}

public sealed record UpdateRuntimeSnapshot(
    UpdateRuntimeStatus Status,
    string CurrentVersion,
    string? AvailableVersion,
    DateTimeOffset? LastCheckedAtUtc,
    string? ReleaseNotesUrl,
    double? Progress,
    string? SafeErrorCode,
    bool CanPrepare = false,
    bool CanInstall = false);

public sealed class UpdateRuntimeState
{
    private UpdateRuntimeSnapshot _current;

    public UpdateRuntimeState(string currentVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);
        _current = new UpdateRuntimeSnapshot(
            UpdateRuntimeStatus.Ready,
            currentVersion,
            null,
            null,
            null,
            null,
            null);
    }

    public event EventHandler<UpdateRuntimeSnapshot>? Changed;

    public UpdateRuntimeSnapshot Current => Volatile.Read(ref _current);

    public void Set(UpdateRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _current, snapshot);
        Changed?.Invoke(this, snapshot);
    }
}
