using CodexUsageMonitor.Application.Monitoring;
using CodexUsageMonitor.Core.Abstractions;
using CodexUsageMonitor.Core.Notifications;
using CodexUsageMonitor.Core.Settings;
using CodexUsageMonitor.Core.Usage;
using CodexUsageMonitor.Email.Outbox;
using CodexUsageMonitor.Email.Templates;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.App.Services;

public sealed class TransitionEmailDispatcher : IUsageEmailSink
{
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromDays(2);
    private readonly EmailOutboxQueue _outbox;
    private readonly ApplicationSettingsService _settings;
    private readonly IClock _clock;
    private readonly ILogger<TransitionEmailDispatcher> _logger;

    public TransitionEmailDispatcher(
        EmailOutboxQueue outbox,
        ApplicationSettingsService settings,
        IClock clock,
        ILogger<TransitionEmailDispatcher> logger)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsConfigured
    {
        get
        {
            var email = _settings.Current.Email;
            return email.Provider is not EmailProviderMode.Disabled
                && !string.IsNullOrWhiteSpace(email.SenderAddress)
                && email.Recipients.Count > 0;
        }
    }

    public async Task<bool> QueueAsync(
        UsageTransition transition,
        UsageSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transition);
        ArgumentNullException.ThrowIfNull(snapshot);
        var settings = _settings.Current;
        var email = settings.Email;
        if (email.Provider is EmailProviderMode.Disabled
            || string.IsNullOrWhiteSpace(email.SenderAddress)
            || email.Recipients.Count == 0)
        {
            return false;
        }

        var now = _clock.UtcNow;
        if (transition.ExpiresAtUtc <= now)
        {
            return false;
        }

        var quietHours = new QuietHoursSchedule(
            settings.Notifications.QuietHoursEnabled,
            settings.Notifications.QuietHoursStart,
            settings.Notifications.QuietHoursEnd);
        var availableAtUtc = quietHours.IsQuiet(now, _clock.LocalTimeZone)
            ? quietHours.NextEnd(now, _clock.LocalTimeZone)
            : now;
        var expiresAtUtc = transition.ExpiresAtUtc < now + MaximumLifetime
            ? transition.ExpiresAtUtc
            : now + MaximumLifetime;
        if (availableAtUtc >= expiresAtUtc)
        {
            _logger.LogInformation(
                "Email event {EventIdentity} expired during quiet hours and was not queued.",
                transition.Identity.Value);
            return false;
        }

        var accountLabel = settings.General.PrivacyMode
            ? snapshot.Account.SafeLabel
            : snapshot.Account.DisplayName ?? snapshot.Account.Email ?? snapshot.Account.SafeLabel;
        var message = UsageEmailTemplate.Create(
            transition,
            email.SenderAddress,
            email.Recipients,
            email.IncludeAccountLabel,
            accountLabel);
        var queued = await _outbox.EnqueueAsync(
            message,
            snapshot.ProfileId,
            snapshot.Account.StorageKey,
            availableAtUtc,
            expiresAtUtc,
            cancellationToken).ConfigureAwait(false);
        if (queued)
        {
            _logger.LogInformation("Queued email event {EventIdentity}.", transition.Identity.Value);
        }

        return queued;
    }
}
