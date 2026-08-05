using CodexUsageMonitor.Core.Settings;

namespace CodexUsageMonitor.Application.Settings;

public sealed class ApplicationSettingsService : IApplicationSettingsSnapshot
{
    private readonly ISettingsStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings _current = new();
    private int _canPersist = 1;

    public ApplicationSettingsService(ISettingsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public AppSettings Current => Volatile.Read(ref _current);

    public bool CanPersist => Volatile.Read(ref _canPersist) != 0;

    public event EventHandler<AppSettings>? Changed;

    public async Task<SettingsValidationResult> InitializeAsync(CancellationToken cancellationToken)
    {
        var result = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _current, result.Settings);
        Volatile.Write(ref _canPersist, result.CanPersist ? 1 : 0);
        Changed?.Invoke(this, result.Settings);
        return result;
    }

    public async Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!CanPersist)
        {
            throw new InvalidOperationException("Settings were created by a newer application version and cannot be overwritten.");
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var next = SettingsValidation.Normalize(update(Current)).Settings;
            await _store.SaveAsync(next, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _current, next);
            Changed?.Invoke(this, next);
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }
}
