using CodexUsageMonitor.Core.Abstractions;

namespace CodexUsageMonitor.Application.Runtime;

public enum ApplicationStartupStage
{
    NotStarted,
    SingleInstance,
    HostConstruction,
    UpdateRecovery,
    DataInitialization,
    SettingsInitialization,
    ThemeInitialization,
    NativeNotificationRegistration,
    StartupRegistration,
    SystemEvents,
    BackgroundServices,
    Monitoring,
    UserInterface,
    Ready,
    Stopping,
    Stopped,
    Failed,
}

public sealed record DegradedComponent(string Component, string SafeErrorCode);

public sealed class ApplicationStartupState
{
    private readonly object _gate = new();
    private readonly List<DegradedComponent> _degraded = [];
    private readonly IClock _clock;
    private ApplicationStartupStage _stage;
    private DateTimeOffset? _startedAtUtc;
    private DateTimeOffset? _completedAtUtc;
    private string? _safeFailureCode;

    public ApplicationStartupState(IClock clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public ApplicationStartupStage Stage { get { lock (_gate) return _stage; } }
    public DateTimeOffset? StartedAtUtc { get { lock (_gate) return _startedAtUtc; } }
    public DateTimeOffset? CompletedAtUtc { get { lock (_gate) return _completedAtUtc; } }
    public string? SafeFailureCode { get { lock (_gate) return _safeFailureCode; } }
    public IReadOnlyList<DegradedComponent> DegradedComponents { get { lock (_gate) return _degraded.ToArray(); } }
    public bool IsReady => Stage is ApplicationStartupStage.Ready;
    public bool IsUpdateHealthEligible => IsReady && SafeFailureCode is null;

    public void Begin() => Begin(_clock.UtcNow);

    public void Begin(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            _startedAtUtc ??= nowUtc.ToUniversalTime();
            _stage = ApplicationStartupStage.SingleInstance;
        }
    }

    public void Advance(ApplicationStartupStage stage)
    {
        lock (_gate)
        {
            if (_stage is ApplicationStartupStage.Failed or ApplicationStartupStage.Stopped)
            {
                return;
            }

            if (stage < _stage && stage is not ApplicationStartupStage.Stopping)
            {
                throw new InvalidOperationException($"Startup stage cannot move backward from {_stage} to {stage}.");
            }

            _stage = stage;
            if (stage is ApplicationStartupStage.Ready or ApplicationStartupStage.Stopped)
            {
                _completedAtUtc = _clock.UtcNow;
            }
        }
    }

    public void AddDegraded(string component, string safeErrorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        ArgumentException.ThrowIfNullOrWhiteSpace(safeErrorCode);
        lock (_gate)
        {
            var normalized = component.Trim();
            var replacement = new DegradedComponent(normalized, safeErrorCode.Trim());
            var existing = _degraded.FindIndex(item =>
                string.Equals(item.Component, normalized, StringComparison.OrdinalIgnoreCase));
            if (existing < 0)
            {
                _degraded.Add(replacement);
            }
            else
            {
                _degraded[existing] = replacement;
            }
        }
    }

    public void ClearDegraded(string component)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        lock (_gate)
        {
            _degraded.RemoveAll(item =>
                string.Equals(item.Component, component.Trim(), StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Fail(string safeErrorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeErrorCode);
        lock (_gate)
        {
            _safeFailureCode = safeErrorCode.Trim();
            _stage = ApplicationStartupStage.Failed;
            _completedAtUtc = _clock.UtcNow;
        }
    }
}
