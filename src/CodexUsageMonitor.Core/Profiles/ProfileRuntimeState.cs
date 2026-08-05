using CodexUsageMonitor.Core.Accounts;

namespace CodexUsageMonitor.Core.Profiles;

public enum ProfileConnectionState
{
    Stopped,
    Starting,
    Connected,
    Retrying,
    AuthenticationRequired,
    CodexUnavailable,
    Faulted,
}

public sealed record ProfileRuntimeState(
    Guid ProfileId,
    ProfileConnectionState ConnectionState,
    AccountIdentity? Account,
    DateTimeOffset? LastConnectedAtUtc,
    DateTimeOffset? LastDataAtUtc,
    string? SafeStatusCode,
    int ConsecutiveFailures)
{
    public static ProfileRuntimeState Initial(Guid profileId) =>
        new(profileId, ProfileConnectionState.Stopped, null, null, null, null, 0);
}
