using System.Windows.Input;
using System.Windows.Threading;
using CodexUsageMonitor.App.Formatting;
using CodexUsageMonitor.App.Infrastructure;
using CodexUsageMonitor.App.Services;
using CodexUsageMonitor.Codex.Monitoring;
using CodexUsageMonitor.Core.Abstractions;
using CodexUsageMonitor.Core.Monitoring;
using CodexUsageMonitor.Core.ResetCredits;
using CodexUsageMonitor.Core.Scheduling;
using CodexUsageMonitor.Core.Settings;
using CodexUsageMonitor.Core.Usage;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.App.ViewModels;

public enum WidgetVisualState
{
    Starting,
    Healthy,
    Warning,
    Critical,
    Depleted,
    Stale,
    Error,
}

public sealed record WidgetActions(
    Action OpenSettings,
    Action ShowWidget,
    Action Exit,
    Func<ResetCredit, Task>? RedeemResetCredit = null);

public sealed class WidgetViewModel : ObservableObject, IDisposable
{
    private readonly UsageApplicationState _applicationState;
    private readonly ApplicationSettingsService _settings;
    private readonly MultiProfileMonitorCoordinator _monitors;
    private readonly IClock _clock;
    private readonly WidgetActions _actions;
    private readonly ILogger<WidgetViewModel> _logger;
    private readonly DispatcherTimer _clockTimer;
    private bool _isVisible = true;
    private bool _isHovering;
    private WidgetSize _size;
    private double _width;
    private double _height;
    private decimal _remainingPercent;
    private decimal _secondaryRemainingPercent;
    private string _remainingText = "--%";
    private string _secondaryRemainingText = string.Empty;
    private string _limitLabel = "Connecting";
    private string _secondaryLimitLabel = string.Empty;
    private string _resetText = "Waiting for Codex";
    private string _accountText = "Codex account";
    private string _statusText = "Starting";
    private string _toolTipText = "Codex Usage Monitor is starting.";
    private bool _hasData;
    private bool _hasSecondary;
    private bool _isLocked;
    private bool _isClickThrough;
    private bool _showAccount;
    private bool _showResetTime;
    private bool _canRedeemResetCredit;
    private ResetCredit? _resetCredit;
    private WidgetVisualState _visualState = WidgetVisualState.Starting;

    public WidgetViewModel(
        UsageApplicationState applicationState,
        ApplicationSettingsService settings,
        MultiProfileMonitorCoordinator monitors,
        IClock clock,
        WidgetActions actions,
        ILogger<WidgetViewModel> logger)
    {
        _applicationState = applicationState ?? throw new ArgumentNullException(nameof(applicationState));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _monitors = monitors ?? throw new ArgumentNullException(nameof(monitors));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        RefreshCommand = new RelayCommand(Refresh);
        OpenSettingsCommand = new RelayCommand(_actions.OpenSettings);
        ShowWidgetCommand = new RelayCommand(_actions.ShowWidget);
        ExitCommand = new RelayCommand(_actions.Exit);
        SetSizeCommand = new ParameterRelayCommand<WidgetSize>(SetSize);
        ToggleLockCommand = new RelayCommand(ToggleLock);
        ToggleClickThroughCommand = new RelayCommand(ToggleClickThrough);
        RedeemResetCreditCommand = new AsyncRelayCommand(RedeemResetCreditAsync, () => CanRedeemResetCredit, ReportCommandFailure);
        _applicationState.SnapshotChanged += OnStateChanged;
        _applicationState.MonitorStateChanged += OnStateChanged;
        _applicationState.ActiveProfileChanged += OnStateChanged;
        _settings.Changed += OnSettingsChanged;
        _clockTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMinutes(1),
        };
        _clockTimer.Tick += OnClockTick;
        ApplySettings(_settings.Current);
        Recompute();
        ScheduleNextTick();
    }

    public ICommand RefreshCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand ShowWidgetCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand SetSizeCommand { get; }
    public ICommand ToggleLockCommand { get; }
    public ICommand ToggleClickThroughCommand { get; }
    public ICommand RedeemResetCreditCommand { get; }

    public WidgetSize Size { get => _size; private set => SetProperty(ref _size, value); }
    public double Width { get => _width; private set => SetProperty(ref _width, value); }
    public double Height { get => _height; private set => SetProperty(ref _height, value); }
    public decimal RemainingPercent { get => _remainingPercent; private set => SetProperty(ref _remainingPercent, value); }
    public decimal SecondaryRemainingPercent { get => _secondaryRemainingPercent; private set => SetProperty(ref _secondaryRemainingPercent, value); }
    public string RemainingText { get => _remainingText; private set => SetProperty(ref _remainingText, value); }
    public string SecondaryRemainingText { get => _secondaryRemainingText; private set => SetProperty(ref _secondaryRemainingText, value); }
    public string LimitLabel { get => _limitLabel; private set => SetProperty(ref _limitLabel, value); }
    public string SecondaryLimitLabel { get => _secondaryLimitLabel; private set => SetProperty(ref _secondaryLimitLabel, value); }
    public string ResetText { get => _resetText; private set => SetProperty(ref _resetText, value); }
    public string AccountText { get => _accountText; private set => SetProperty(ref _accountText, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string ToolTipText { get => _toolTipText; private set => SetProperty(ref _toolTipText, value); }
    public bool HasData { get => _hasData; private set => SetProperty(ref _hasData, value); }
    public bool HasSecondary { get => _hasSecondary; private set => SetProperty(ref _hasSecondary, value); }
    public bool IsLocked { get => _isLocked; private set => SetProperty(ref _isLocked, value); }
    public bool IsClickThrough { get => _isClickThrough; private set => SetProperty(ref _isClickThrough, value); }
    public bool ShowAccount { get => _showAccount; private set => SetProperty(ref _showAccount, value); }
    public bool ShowResetTime { get => _showResetTime; private set => SetProperty(ref _showResetTime, value); }
    public bool CanRedeemResetCredit { get => _canRedeemResetCredit; private set { if (SetProperty(ref _canRedeemResetCredit, value)) ((AsyncRelayCommand)RedeemResetCreditCommand).RaiseCanExecuteChanged(); } }
    public WidgetVisualState VisualState { get => _visualState; private set => SetProperty(ref _visualState, value); }


    public void SetPresentationState(bool isVisible, bool isHovering)
    {
        _isVisible = isVisible;
        _isHovering = isHovering;
        RunOnDispatcher(() =>
        {
            Recompute();
            ScheduleNextTick();
        });
    }

    private void Recompute()
    {
        var snapshot = _applicationState.ActiveSnapshot;
        var state = _applicationState.ActiveMonitorState;
        var settings = _settings.Current;
        var effectiveConnection = EffectiveConnection(state, _clock.UtcNow);
        if (snapshot is null)
        {
            HasData = false;
            HasSecondary = false;
            RemainingText = "--%";
            RemainingPercent = 0;
            LimitLabel = StatusFor(effectiveConnection);
            ResetText = state.SafeErrorCode is null ? "Waiting for a confirmed reading" : "Last reading unavailable";
            AccountText = "Codex account";
            StatusText = StatusFor(effectiveConnection);
            VisualState = VisualFor(effectiveConnection, null);
            ToolTipText = $"{StatusText}\n{UsageDisplayFormatter.Age(state.LastSuccessAtUtc, _clock.UtcNow)}";
            CanRedeemResetCredit = false;
            _resetCredit = null;
            return;
        }

        var selection = LimitSelectionEngine.Select(snapshot.Limits, new LimitSelectionRequest(
            settings.Limits.SelectionMode,
            settings.Limits.ExplicitLimitIdentity,
            settings.Limits.PreferredModel,
            settings.Widget.Size is WidgetSize.Medium && settings.Limits.MediumDualMeter));
        var primary = selection.Primary;
        var secondary = selection.Secondary;
        HasData = primary is not null;
        if (primary is not null)
        {
            RemainingPercent = primary.RemainingPercent;
            RemainingText = UsageDisplayFormatter.Percentage(primary.RemainingPercent);
            LimitLabel = primary.Label;
            ResetText = UsageDisplayFormatter.Reset(primary.ResetsAtUtc, _clock.UtcNow);
        }
        else
        {
            RemainingPercent = 0;
            RemainingText = "--%";
            LimitLabel = "No limits available";
            ResetText = "Codex returned no displayable limit";
        }

        HasSecondary = secondary is not null;
        SecondaryRemainingPercent = secondary?.RemainingPercent ?? 0m;
        SecondaryRemainingText = secondary is null ? string.Empty : UsageDisplayFormatter.Percentage(secondary.RemainingPercent);
        SecondaryLimitLabel = secondary?.Label ?? string.Empty;
        AccountText = settings.General.PrivacyMode ? snapshot.Account.SafeLabel : snapshot.Account.DisplayName ?? snapshot.Account.Email ?? snapshot.Account.SafeLabel;
        StatusText = StatusFor(effectiveConnection);
        VisualState = VisualFor(effectiveConnection, primary?.RemainingPercent);
        ToolTipText = string.Join("\n", new[]
        {
            $"{LimitLabel}: {RemainingText} remaining",
            ResetText,
            UsageDisplayFormatter.Age(snapshot.ObservedAtUtc, _clock.UtcNow),
            $"Status: {StatusText}",
            $"Account: {AccountText}",
        });
        _resetCredit = snapshot.AvailableResetCredits.FirstOrDefault(static credit => credit.IsRedeemable);
        CanRedeemResetCredit = _resetCredit is not null && effectiveConnection is MonitorConnectionState.Live && !_resetCredit.IsExpired(_clock.UtcNow);
    }

    private void ScheduleNextTick()
    {
        var dispatcher = _clockTimer.Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        var resetAtUtc = _applicationState.ActiveSnapshot is { } snapshot
            ? LimitSelectionEngine.Select(snapshot.Limits, new LimitSelectionRequest(
                _settings.Current.Limits.SelectionMode,
                _settings.Current.Limits.ExplicitLimitIdentity,
                _settings.Current.Limits.PreferredModel,
                false)).Primary?.ResetsAtUtc
            : null;
        var delay = AdaptiveUiScheduler.NextDelay(
            _applicationState.ActiveMonitorState,
            _isVisible,
            _isHovering,
            _clock.UtcNow,
            resetAtUtc);
        if (delay is null)
        {
            _clockTimer.Stop();
            return;
        }

        _clockTimer.Interval = delay.Value;
        if (!_clockTimer.IsEnabled)
        {
            _clockTimer.Start();
        }
    }

    private void ApplySettings(AppSettings settings)
    {
        Size = settings.Widget.Size;
        (Width, Height) = settings.Widget.Size switch
        {
            WidgetSize.Medium => (208d, 60d),
            WidgetSize.Small => (148d, 42d),
            _ => (104d, 30d),
        };
        IsLocked = settings.Widget.Locked;
        IsClickThrough = settings.Widget.ClickThrough;
        ShowAccount = settings.Widget.ShowAccountLabel;
        ShowResetTime = settings.Widget.ResetTimeDisplay is not ResetTimeDisplayMode.Hidden;
    }

    private void Refresh()
    {
        if (_applicationState.ActiveProfileId is { } profileId)
        {
            _monitors.RequestRefresh(profileId);
        }
        else
        {
            _monitors.RequestRefreshAll();
        }
    }

    private void SetSize(WidgetSize size) =>
        PersistWidgetSettingsAsync(current => current with { Widget = current.Widget with { Size = size } });

    private void ToggleLock() =>
        PersistWidgetSettingsAsync(current => current with { Widget = current.Widget with { Locked = !current.Widget.Locked } });

    private void ToggleClickThrough() =>
        PersistWidgetSettingsAsync(current => current with { Widget = current.Widget with { ClickThrough = !current.Widget.ClickThrough } });

    private async void PersistWidgetSettingsAsync(Func<AppSettings, AppSettings> update)
    {
        try
        {
            await _settings.UpdateAsync(update, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            ReportCommandFailure(exception);
        }
    }

    private async Task RedeemResetCreditAsync(CancellationToken cancellationToken)
    {
        if (_resetCredit is not null && _actions.RedeemResetCredit is not null)
        {
            await _actions.RedeemResetCredit(_resetCredit).WaitAsync(cancellationToken);
        }
    }

    private void ReportCommandFailure(Exception exception)
    {
        _logger.LogWarning(exception, "A widget command failed safely.");
        StatusText = "Action unavailable";
        ToolTipText = "The requested widget action could not be completed. Open Diagnostics for details.";
    }


    private static MonitorConnectionState EffectiveConnection(MonitorState state, DateTimeOffset nowUtc)
    {
        if (state.Connection is MonitorConnectionState.AuthenticationRequired
            or MonitorConnectionState.CodexUnavailable
            or MonitorConnectionState.Faulted
            or MonitorConnectionState.Retrying)
        {
            return state.Connection;
        }

        if (state.LastSuccessAtUtc is not { } lastSuccessAtUtc)
        {
            return state.Connection;
        }

        var age = nowUtc - lastSuccessAtUtc;
        if (age < TimeSpan.Zero || (age > TimeSpan.FromMinutes(2) && age <= TimeSpan.FromMinutes(10)))
        {
            return MonitorConnectionState.Delayed;
        }

        return age > TimeSpan.FromMinutes(10)
            ? MonitorConnectionState.Stale
            : MonitorConnectionState.Live;
    }

    private static string StatusFor(MonitorConnectionState state) => state switch
    {
        MonitorConnectionState.Starting => "Starting",
        MonitorConnectionState.Live => "Live",
        MonitorConnectionState.Delayed => "Delayed",
        MonitorConnectionState.Stale => "Stale",
        MonitorConnectionState.Retrying => "Reconnecting",
        MonitorConnectionState.AuthenticationRequired => "Sign in required",
        MonitorConnectionState.CodexUnavailable => "Codex not found",
        _ => "Unavailable",
    };

    private static WidgetVisualState VisualFor(MonitorConnectionState connection, decimal? remaining) => connection switch
    {
        MonitorConnectionState.Stale or MonitorConnectionState.Delayed => WidgetVisualState.Stale,
        MonitorConnectionState.Retrying or MonitorConnectionState.AuthenticationRequired or MonitorConnectionState.CodexUnavailable or MonitorConnectionState.Faulted => WidgetVisualState.Error,
        _ when remaining is null => WidgetVisualState.Starting,
        _ when remaining <= 0 => WidgetVisualState.Depleted,
        _ when remaining <= 10 => WidgetVisualState.Critical,
        _ when remaining <= 20 => WidgetVisualState.Warning,
        _ => WidgetVisualState.Healthy,
    };

    private int _refreshQueued;

    private void OnStateChanged(object? sender, EventArgs eventArgs) => QueueRefresh();
    private void OnStateChanged(object? sender, UsageSnapshot snapshot) => QueueRefresh();
    private void OnStateChanged(object? sender, (Guid ProfileId, MonitorState State) state) => QueueRefresh();
    private void OnStateChanged(object? sender, Guid? profileId) => QueueRefresh();
    private void OnSettingsChanged(object? sender, AppSettings settings) => RunOnDispatcher(() =>
    {
        ApplySettings(settings);
        Recompute();
        ScheduleNextTick();
    });

    private void OnClockTick(object? sender, EventArgs eventArgs)
    {
        _clockTimer.Stop();
        Recompute();
        ScheduleNextTick();
    }

    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0)
        {
            return;
        }

        var dispatcher = _clockTimer.Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            return;
        }

        _ = dispatcher.InvokeAsync(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            Recompute();
            ScheduleNextTick();
        }, DispatcherPriority.Background);
    }

    private void RunOnDispatcher(Action action)
    {
        var dispatcher = _clockTimer.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
        {
            _ = dispatcher.InvokeAsync(action, DispatcherPriority.Background);
        }
    }

    public void Dispose()
    {
        _clockTimer.Stop();
        _clockTimer.Tick -= OnClockTick;
        _applicationState.SnapshotChanged -= OnStateChanged;
        _applicationState.MonitorStateChanged -= OnStateChanged;
        _applicationState.ActiveProfileChanged -= OnStateChanged;
        _settings.Changed -= OnSettingsChanged;
    }
}
