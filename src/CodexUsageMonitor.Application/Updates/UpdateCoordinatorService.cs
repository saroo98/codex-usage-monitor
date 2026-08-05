using System.Security.Cryptography;
using CodexUsageMonitor.Application.Diagnostics;
using CodexUsageMonitor.Application.Settings;
using CodexUsageMonitor.Core.Abstractions;

namespace CodexUsageMonitor.Application.Updates;

public sealed class UpdateCoordinatorService
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly IUpdatePlatformPort _platform;
    private readonly ApplicationSettingsService _settings;
    private readonly UpdateRuntimeState _state;
    private readonly IClock _clock;
    private readonly IApplicationFailureSink _failures;

    public UpdateCoordinatorService(
        IUpdatePlatformPort platform,
        ApplicationSettingsService settings,
        UpdateRuntimeState state,
        IClock clock,
        IApplicationFailureSink failures)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _failures = failures ?? throw new ArgumentNullException(nameof(failures));
    }

    public UpdateRuntimeSnapshot Current => _state.Current;

    public async Task<UpdateRuntimeSnapshot> CheckAsync(bool manual, CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_platform.IsManagedExternally)
            {
                return Set(CreateManagedExternallySnapshot());
            }

            var updateSettings = _settings.Current.Updates;
            if (updateSettings.ManifestUri is null)
            {
                return Set(new UpdateRuntimeSnapshot(
                    UpdateRuntimeStatus.NotConfigured,
                    _state.Current.CurrentVersion,
                    null,
                    updateSettings.LastCheckAtUtc,
                    null,
                    null,
                    "update.manifest_not_configured"));
            }

            var previous = _state.Current;
            Set(previous with
            {
                Status = UpdateRuntimeStatus.Checking,
                Progress = null,
                SafeErrorCode = null,
                CanPrepare = false,
                CanInstall = false,
            });

            try
            {
                var result = await _platform.CheckAsync(
                    updateSettings.ManifestUri,
                    updateSettings.Channel.ToString().ToLowerInvariant(),
                    manual ? null : updateSettings.ManifestEntityTag,
                    cancellationToken).ConfigureAwait(false);
                var now = _clock.UtcNow;
                var snapshot = Map(result, now);
                await PersistCheckMetadataAsync(result, now, cancellationToken).ConfigureAwait(false);
                return Set(snapshot);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Set(previous);
                throw;
            }
            catch (Exception exception) when (IsExpectedUpdateFailure(exception))
            {
                var safeCode = SafeErrorCode(exception);
                _failures.Report(safeCode, exception);
                await PersistFailureAttemptAsync(cancellationToken).ConfigureAwait(false);
                return Set(_state.Current with
                {
                    Status = UpdateRuntimeStatus.Failed,
                    LastCheckedAtUtc = _clock.UtcNow,
                    Progress = null,
                    SafeErrorCode = safeCode,
                    CanPrepare = false,
                    CanInstall = false,
                });
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<UpdateRuntimeSnapshot> PrepareAsync(CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_platform.IsManagedExternally)
            {
                return Set(CreateManagedExternallySnapshot());
            }

            var current = _state.Current;
            if (current.Status is not UpdateRuntimeStatus.Available || !_platform.HasVerifiedCandidate)
            {
                return Set(current with { SafeErrorCode = "update.no_verified_asset", CanPrepare = false });
            }

            Set(current with { Status = UpdateRuntimeStatus.Downloading, Progress = 0, SafeErrorCode = null, CanPrepare = false });
            var progress = new InlineProgress<double>(value => Set(_state.Current with
            {
                Status = UpdateRuntimeStatus.Downloading,
                Progress = Math.Clamp(value, 0, 1),
            }));
            try
            {
                await _platform.PrepareAsync(progress, cancellationToken).ConfigureAwait(false);
                return Set(_state.Current with
                {
                    Status = UpdateRuntimeStatus.Staged,
                    Progress = 1,
                    SafeErrorCode = null,
                    CanPrepare = false,
                    CanInstall = _platform.HasPreparedUpdate,
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Set(current with
                {
                    Status = UpdateRuntimeStatus.Available,
                    Progress = null,
                    SafeErrorCode = null,
                    CanPrepare = _platform.HasVerifiedCandidate,
                    CanInstall = false,
                });
                throw;
            }
            catch (Exception exception) when (IsExpectedUpdateFailure(exception))
            {
                var safeCode = SafeErrorCode(exception);
                _failures.Report(safeCode, exception);
                return Set(_state.Current with
                {
                    Status = UpdateRuntimeStatus.Failed,
                    Progress = null,
                    SafeErrorCode = safeCode,
                    CanInstall = false,
                });
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<UpdateRuntimeSnapshot> InstallPreparedAsync(CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _state.Current;
            if (_platform.IsManagedExternally)
            {
                return Set(CreateManagedExternallySnapshot());
            }

            if (current.Status is not UpdateRuntimeStatus.Staged || !_platform.HasPreparedUpdate)
            {
                return Set(current with { SafeErrorCode = "update.not_staged", CanInstall = false });
            }

            Set(current with { Status = UpdateRuntimeStatus.Installing, SafeErrorCode = null, CanInstall = false });
            try
            {
                await _platform.LaunchPreparedAsync(cancellationToken).ConfigureAwait(false);
                return _state.Current;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Set(current with
                {
                    Status = UpdateRuntimeStatus.Staged,
                    SafeErrorCode = null,
                    CanInstall = _platform.HasPreparedUpdate,
                });
                throw;
            }
            catch (Exception exception) when (IsExpectedUpdateFailure(exception))
            {
                var safeCode = SafeErrorCode(exception);
                _failures.Report(safeCode, exception);
                return Set(_state.Current with { Status = UpdateRuntimeStatus.Failed, SafeErrorCode = safeCode });
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private UpdateRuntimeSnapshot Map(UpdateCheckOutcome result, DateTimeOffset checkedAtUtc)
    {
        var current = _state.Current;
        return result.Status switch
        {
            UpdateCheckOutcomeStatus.NotModified => current with
            {
                Status = current.AvailableVersion is null ? UpdateRuntimeStatus.Current : UpdateRuntimeStatus.Available,
                LastCheckedAtUtc = checkedAtUtc,
                SafeErrorCode = null,
                Progress = null,
                CanPrepare = _platform.HasVerifiedCandidate,
            },
            UpdateCheckOutcomeStatus.Current => new UpdateRuntimeSnapshot(
                UpdateRuntimeStatus.Current, current.CurrentVersion, null, checkedAtUtc,
                result.ReleaseNotesUrl, null, null),
            UpdateCheckOutcomeStatus.Available => new UpdateRuntimeSnapshot(
                UpdateRuntimeStatus.Available, current.CurrentVersion, result.AvailableVersion, checkedAtUtc,
                result.ReleaseNotesUrl, null, result.HasVerifiedAsset ? null : "update.asset_missing",
                CanPrepare: result.HasVerifiedAsset),
            UpdateCheckOutcomeStatus.UnsupportedOperatingSystem => new UpdateRuntimeSnapshot(
                UpdateRuntimeStatus.UnsupportedOperatingSystem, current.CurrentVersion, result.AvailableVersion, checkedAtUtc,
                result.ReleaseNotesUrl, null, "update.os_unsupported"),
            UpdateCheckOutcomeStatus.UnsupportedArchitecture => new UpdateRuntimeSnapshot(
                UpdateRuntimeStatus.UnsupportedArchitecture, current.CurrentVersion, result.AvailableVersion, checkedAtUtc,
                result.ReleaseNotesUrl, null, "update.architecture_unsupported"),
            _ => throw new InvalidOperationException("Unknown update status."),
        };
    }

    private async Task PersistCheckMetadataAsync(UpdateCheckOutcome result, DateTimeOffset checkedAtUtc, CancellationToken cancellationToken)
    {
        if (!_settings.CanPersist)
        {
            return;
        }

        try
        {
            await _settings.UpdateAsync(current => current with
            {
                Updates = current.Updates with
                {
                    LastCheckAtUtc = checkedAtUtc,
                    ManifestEntityTag = result.EntityTag ?? current.Updates.ManifestEntityTag,
                    LastOfferedVersion = result.Status is UpdateCheckOutcomeStatus.Available ? result.AvailableVersion : null,
                },
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _failures.Report("update.metadata_persist_failed", exception);
        }
    }

    private async Task PersistFailureAttemptAsync(CancellationToken cancellationToken)
    {
        if (!_settings.CanPersist)
        {
            return;
        }

        try
        {
            await _settings.UpdateAsync(current => current with
            {
                Updates = current.Updates with { LastCheckAtUtc = _clock.UtcNow },
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _failures.Report("update.metadata_persist_failed", exception);
        }
    }

    private UpdateRuntimeSnapshot CreateManagedExternallySnapshot() => _state.Current with
    {
        Status = UpdateRuntimeStatus.ManagedExternally,
        Progress = null,
        SafeErrorCode = "update.msix_managed_externally",
        CanPrepare = false,
        CanInstall = false,
    };

    private UpdateRuntimeSnapshot Set(UpdateRuntimeSnapshot snapshot)
    {
        _state.Set(snapshot);
        return snapshot;
    }

    private static bool IsExpectedUpdateFailure(Exception exception) => exception is
        IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or
        HttpRequestException or CryptographicException or System.ComponentModel.Win32Exception;

    private static string SafeErrorCode(Exception exception) => exception switch
    {
        HttpRequestException => "update.network_failed",
        CryptographicException => "update.trust_failed",
        UnauthorizedAccessException => "update.access_denied",
        InvalidDataException => "update.invalid_data",
        IOException => "update.io_failed",
        System.ComponentModel.Win32Exception => "update.windows_failed",
        _ => "update.failed",
    };

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        private readonly Action<T> _callback = callback ?? throw new ArgumentNullException(nameof(callback));

        public void Report(T value) => _callback(value);
    }
}
