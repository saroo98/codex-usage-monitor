using System.Collections.Concurrent;
using CodexUsageMonitor.Application.Diagnostics;
using CodexUsageMonitor.Application.Runtime;
using CodexUsageMonitor.Application.Settings;
using CodexUsageMonitor.Core.Abstractions;
using CodexUsageMonitor.Core.Monitoring;
using CodexUsageMonitor.Core.Notifications;
using CodexUsageMonitor.Core.Profiles;
using CodexUsageMonitor.Core.Usage;

namespace CodexUsageMonitor.Application.Monitoring;

public sealed class UsageApplicationState : IProfileMonitorCallbacks, IUsageRuntimeSnapshotProvider
{
    private readonly ConcurrentDictionary<Guid, UsageSnapshot> _snapshots = new();
    private readonly ConcurrentDictionary<Guid, MonitorState> _states = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastHistoryAt = new(StringComparer.Ordinal);
    private readonly IUsageHistoryWriter _history;
    private readonly IUsageNotificationSink _notifications;
    private readonly IUsageEmailSink _email;
    private readonly IApplicationSettingsSnapshot _settings;
    private readonly IClock _clock;
    private readonly IApplicationEventDispatcher _events;
    private readonly IApplicationFailureSink _failures;
    private Guid? _activeProfileId;

    public UsageApplicationState(
        IUsageHistoryWriter history,
        IUsageNotificationSink notifications,
        IUsageEmailSink email,
        IApplicationSettingsSnapshot settings,
        IClock clock,
        IApplicationEventDispatcher events,
        IApplicationFailureSink failures)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _email = email ?? throw new ArgumentNullException(nameof(email));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _failures = failures ?? throw new ArgumentNullException(nameof(failures));
    }

    public event EventHandler<UsageSnapshot>? SnapshotChanged;
    public event EventHandler<(Guid ProfileId, MonitorState State)>? MonitorStateChanged;
    public event EventHandler<Guid?>? ActiveProfileChanged;

    public Guid? ActiveProfileId => _activeProfileId;

    public UsageSnapshot? ActiveSnapshot =>
        _activeProfileId is { } id && _snapshots.TryGetValue(id, out var snapshot)
            ? snapshot
            : _snapshots.Values.OrderByDescending(static value => value.ObservedAtUtc).FirstOrDefault();

    public MonitorState ActiveMonitorState =>
        _activeProfileId is { } id && _states.TryGetValue(id, out var state)
            ? state
            : _states.Values.OrderByDescending(static value => value.LastAttemptAtUtc).FirstOrDefault() ?? MonitorState.Initial;

    public IReadOnlyDictionary<Guid, UsageSnapshot> Snapshots => _snapshots;

    public bool TryGetSnapshot(Guid profileId, out UsageSnapshot snapshot) =>
        _snapshots.TryGetValue(profileId, out snapshot!);

    public bool TryGetMonitorState(Guid profileId, out MonitorState state) =>
        _states.TryGetValue(profileId, out state!);

    public void SetActiveProfile(Guid? profileId)
    {
        if (profileId is not null && !_settings.Current.Profiles.Any(profile => profile.Id == profileId))
        {
            throw new ArgumentOutOfRangeException(nameof(profileId));
        }

        if (_activeProfileId == profileId) return;
        _activeProfileId = profileId;
        Post(() => ActiveProfileChanged?.Invoke(this, profileId));
        if (ActiveSnapshot is { } snapshot) Post(() => SnapshotChanged?.Invoke(this, snapshot));
        Post(() => MonitorStateChanged?.Invoke(this, (_activeProfileId ?? Guid.Empty, ActiveMonitorState)));
    }

    public async ValueTask SnapshotReceivedAsync(
        ProfileDefinition profile,
        UsageSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshots.TryGetValue(profile.Id, out var previous);
        _snapshots[profile.Id] = snapshot;
        _activeProfileId ??= profile.Id;
        Post(() => SnapshotChanged?.Invoke(this, snapshot));

        var settings = _settings.Current;
        if (settings.History.Enabled && ShouldRecord(snapshot, settings.History.SampleIntervalMinutes))
        {
            try
            {
                await _history.RecordAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UsageHistoryWriteException)
            {
                _failures.Report("monitoring.history_write_failed", exception, profile.Id);
            }
        }

        if (!settings.Notifications.Enabled && !_email.IsConfigured)
        {
            return;
        }

        var quiet = new QuietHoursSchedule(
            settings.Notifications.QuietHoursEnabled,
            settings.Notifications.QuietHoursStart,
            settings.Notifications.QuietHoursEnd);
        foreach (var transition in ThresholdTransitionEngine.Detect(previous, snapshot, settings.Notifications.Thresholds, _clock.UtcNow))
        {
            if (transition.Identity.EventType is NotificationEventType.Reset && !settings.Notifications.NotifyOnReset)
            {
                continue;
            }

            if (settings.Notifications.Enabled)
            {
                await _notifications.DeliverAsync(
                    transition,
                    quiet,
                    settings.Notifications.PlaySound,
                    cancellationToken).ConfigureAwait(false);
            }

            await _email.QueueAsync(transition, snapshot, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask StateChangedAsync(
        ProfileDefinition profile,
        MonitorState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(state);
        _states.TryGetValue(profile.Id, out var previous);
        _states[profile.Id] = state;
        Post(() => MonitorStateChanged?.Invoke(this, (profile.Id, state)));

        var settings = _settings.Current;
        if ((!settings.Notifications.Enabled && !_email.IsConfigured) || !settings.Notifications.NotifyOnConnectionLoss ||
            !_snapshots.TryGetValue(profile.Id, out var snapshot) || previous is null)
        {
            return;
        }

        var wasHealthy = previous.Connection is MonitorConnectionState.Live or MonitorConnectionState.Delayed;
        var isHealthy = state.Connection is MonitorConnectionState.Live or MonitorConnectionState.Delayed;
        if (wasHealthy == isHealthy)
        {
            return;
        }

        var eventType = isHealthy ? NotificationEventType.ConnectionRestored : NotificationEventType.ConnectionLost;
        var transition = new UsageTransition(
            new NotificationIdentity(
                profile.Id,
                snapshot.Account.StorageKey,
                "connection",
                eventType,
                state.LastAttemptAtUtc?.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "observed"),
            null,
            null,
            _clock.UtcNow,
            null,
            isHealthy ? "notification.connection_restored" : "notification.connection_lost",
            _clock.UtcNow.AddHours(2));
        var quiet = new QuietHoursSchedule(
            settings.Notifications.QuietHoursEnabled,
            settings.Notifications.QuietHoursStart,
            settings.Notifications.QuietHoursEnd);
        if (settings.Notifications.Enabled)
        {
            await _notifications.DeliverAsync(transition, quiet, settings.Notifications.PlaySound, cancellationToken).ConfigureAwait(false);
        }

        await _email.QueueAsync(transition, snapshot, cancellationToken).ConfigureAwait(false);
    }

    private bool ShouldRecord(UsageSnapshot snapshot, int intervalMinutes)
    {
        var key = $"{snapshot.ProfileId:N}:{snapshot.Account.StorageKey}";
        var now = snapshot.ObservedAtUtc;
        while (true)
        {
            if (!_lastHistoryAt.TryGetValue(key, out var previous))
            {
                return _lastHistoryAt.TryAdd(key, now);
            }

            if (now - previous < TimeSpan.FromMinutes(Math.Clamp(intervalMinutes, 1, 60)))
            {
                return false;
            }

            if (_lastHistoryAt.TryUpdate(key, now, previous))
            {
                return true;
            }
        }
    }

    private void Post(Action action) => _events.Post(action);
}
