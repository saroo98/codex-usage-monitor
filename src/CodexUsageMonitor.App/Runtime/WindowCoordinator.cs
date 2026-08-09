using System.Windows;
using CodexUsageMonitor.App.ResetCredits;
using CodexUsageMonitor.App.Services;
using CodexUsageMonitor.App.ViewModels;
using CodexUsageMonitor.App.Views;
using CodexUsageMonitor.Core.ResetCredits;
using CodexUsageMonitor.Core.Settings;

namespace CodexUsageMonitor.App.Runtime;

public sealed class WindowCoordinator : IDisposable
{
    private readonly WidgetWindowSession _widgetWindows;
    private readonly Func<SettingsWindow> _settingsWindowFactory;
    private readonly Func<OnboardingWindow> _onboardingWindowFactory;
    private readonly Func<ResetRedemptionIntent, ResetCreditConfirmationDialog> _resetDialogFactory;
    private readonly ApplicationSettingsService _settings;
    private readonly UsageApplicationState _state;
    private readonly ResetCreditRedemptionService _resetCredits;
    private SettingsWindow? _settingsWindow;
    private bool _disposed;

    public WindowCoordinator(
        WidgetWindowSession widgetWindows,
        Func<SettingsWindow> settingsWindowFactory,
        Func<OnboardingWindow> onboardingWindowFactory,
        Func<ResetRedemptionIntent, ResetCreditConfirmationDialog> resetDialogFactory,
        ApplicationSettingsService settings,
        UsageApplicationState state,
        ResetCreditRedemptionService resetCredits)
    {
        _widgetWindows = widgetWindows ?? throw new ArgumentNullException(nameof(widgetWindows));
        _settingsWindowFactory = settingsWindowFactory ?? throw new ArgumentNullException(nameof(settingsWindowFactory));
        _onboardingWindowFactory = onboardingWindowFactory ?? throw new ArgumentNullException(nameof(onboardingWindowFactory));
        _resetDialogFactory = resetDialogFactory ?? throw new ArgumentNullException(nameof(resetDialogFactory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _resetCredits = resetCredits ?? throw new ArgumentNullException(nameof(resetCredits));
    }

    public event EventHandler<bool>? WidgetVisibilityChanged;

    public bool IsWidgetVisible => _widgetWindows.IsVisible;

    public void ShowWidget()
    {
        ThrowIfDisposed();
        _widgetWindows.Show();
        WidgetVisibilityChanged?.Invoke(this, true);
    }

    public void HideWidget()
    {
        ThrowIfDisposed();
        _widgetWindows.Hide();
        WidgetVisibilityChanged?.Invoke(this, false);
    }

    public void OpenSettings(SettingsSection section = SettingsSection.General)
    {
        ThrowIfDisposed();
        if (_settingsWindow is null)
        {
            var window = _settingsWindowFactory();
            window.Closed += OnSettingsWindowClosed;
            _settingsWindow = window;
        }

        _settingsWindow.ActivateSection(section);
    }

    public bool ShowOnboarding(Window? owner = null)
    {
        ThrowIfDisposed();
        var window = _onboardingWindowFactory();
        if (owner is not null && owner.IsVisible)
        {
            window.Owner = owner;
        }

        var accepted = window.ShowDialog() is true;
        if (accepted && window.OpenSettingsAfterClose)
        {
            OpenSettings();
        }

        return accepted;
    }

    public Task DisableClickThroughAsync(CancellationToken cancellationToken) =>
        _settings.UpdateAsync(current => current with
        {
            Widget = current.Widget with { ClickThrough = false },
        }, cancellationToken);

    public void ReflowWidget() => _widgetWindows.RestorePlacement();


    public async Task ReviewResetCreditAsync(Guid profileId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(profileId, Guid.Empty);

        ThrowIfDisposed();
        if (!_state.TryGetSnapshot(profileId, out var snapshot))
        {
            OpenSettings(SettingsSection.Accounts);
            return;
        }

        var redeemable = snapshot.AvailableResetCredits
            .Where(credit => credit.IsRedeemable && !credit.IsExpired(DateTimeOffset.UtcNow))
            .ToArray();
        if (redeemable.Length != 1)
        {
            _state.SetActiveProfile(profileId);
            OpenSettings(SettingsSection.Accounts);
            return;
        }

        _state.SetActiveProfile(profileId);
        await RedeemResetCreditAsync(redeemable[0], cancellationToken).ConfigureAwait(true);
    }

    public Task RedeemResetCreditAsync(ResetCredit credit) =>
        RedeemResetCreditAsync(credit, CancellationToken.None);

    public async Task RedeemResetCreditAsync(ResetCredit credit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credit);
        ThrowIfDisposed();
        if (_state.ActiveProfileId is not { } profileId)
        {
            MessageBox.Show(
                "A live Codex profile is required before a reset credit can be used.",
                "Reset credit unavailable",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            var intent = await _resetCredits.PrepareAsync(profileId, credit.Id, cancellationToken);
            var dialog = _resetDialogFactory(intent);
            if (_widgetWindows.VisibleOwner is { } owner)
            {
                dialog.Owner = owner;
            }

            if (dialog.ShowDialog() is not true || !dialog.Confirmed)
            {
                return;
            }

            var outcome = await _resetCredits.RedeemAsync(intent, explicitlyConfirmed: true, cancellationToken);
            MessageBox.Show(
                outcome.Succeeded
                    ? outcome.AlreadyRedeemed
                        ? "This reset credit had already been applied. Usage limits were refreshed."
                        : "The reset credit was applied and usage limits were refreshed."
                    : $"The reset credit was not applied ({outcome.Code}).",
                outcome.Succeeded ? "Reset credit applied" : "Reset credit not applied",
                MessageBoxButton.OK,
                outcome.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                $"The reset credit could not be used safely ({exception.Message}).",
                "Reset credit unavailable",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }


    public async Task ReviewResetCreditAsync(Guid profileId)
    {
        ThrowIfDisposed();
        if (profileId == Guid.Empty || !_state.TryGetSnapshot(profileId, out var snapshot))
        {
            return;
        }

        _state.SetActiveProfile(profileId);
        var credit = snapshot.AvailableResetCredits.FirstOrDefault(static candidate => candidate.IsRedeemable);
        if (credit is not null)
        {
            await RedeemResetCreditAsync(credit);
        }
    }

    public void CloseForExit()
    {
        if (_disposed)
        {
            return;
        }

        if (_settingsWindow is not null)
        {
            _settingsWindow.Closed -= OnSettingsWindowClosed;
            _settingsWindow.Close();
            _settingsWindow = null;
        }

        _widgetWindows.CloseForExit();
    }

    private void OnSettingsWindowClosed(object? sender, EventArgs eventArgs)
    {
        if (sender is SettingsWindow window)
        {
            window.Closed -= OnSettingsWindowClosed;
        }

        _settingsWindow = null;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CloseForExit();
        _widgetWindows.Dispose();
        _disposed = true;
    }
}
