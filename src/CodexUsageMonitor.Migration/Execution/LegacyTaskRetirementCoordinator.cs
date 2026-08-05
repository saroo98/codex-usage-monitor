using System.Text.Json;
using System.Text.Json.Serialization;
using CodexUsageMonitor.Core.Abstractions;
using CodexUsageMonitor.Migration.Tasks;
using CodexUsageMonitor.Persistence.Files;
using CodexUsageMonitor.Persistence.Paths;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.Migration.Execution;

public sealed record LegacyTaskRetirementState(
    int SchemaVersion,
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset? RetiredAtUtc,
    DateTimeOffset? RestoredAtUtc,
    IReadOnlyList<LegacyTaskSnapshot> Snapshots,
    IReadOnlyList<LegacyTaskRetirementResult> RetirementResults,
    IReadOnlyList<LegacyTaskRestoreResult> RestoreResults)
{
    [JsonIgnore]
    public bool IsRetired => RetiredAtUtc is not null && RestoredAtUtc is null;

    [JsonIgnore]
    public bool HasExistingTasks => Snapshots.Any(static snapshot => snapshot.Existed);

    [JsonIgnore]
    public bool HasFailures =>
        RetirementResults.Any(static result => result.SafeErrorCode is not null) ||
        RestoreResults.Any(static result => result.SafeErrorCode is not null);
}

public interface ILegacyTaskRetirementCoordinator
{
    string StatePath { get; }

    Task<LegacyTaskRetirementState?> GetStateAsync(CancellationToken cancellationToken);

    Task<LegacyTaskRetirementState> RetireAsync(bool explicitlyConfirmed, CancellationToken cancellationToken);

    Task<LegacyTaskRetirementState> RestoreAsync(bool explicitlyConfirmed, CancellationToken cancellationToken);
}

public sealed class LegacyTaskRetirementCoordinator : ILegacyTaskRetirementCoordinator
{
    private const long MaximumStateBytes = 8L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly ILegacyScheduledTaskController _tasks;
    private readonly AppDataPaths _paths;
    private readonly IClock _clock;
    private readonly ILogger<LegacyTaskRetirementCoordinator> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LegacyTaskRetirementCoordinator(
        ILegacyScheduledTaskController tasks,
        AppDataPaths paths,
        IClock clock,
        ILogger<LegacyTaskRetirementCoordinator> logger)
    {
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string StatePath => Path.Combine(_paths.Root, "migration", "legacy-task-retirement.json");

    public async Task<LegacyTaskRetirementState?> GetStateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadStateCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LegacyTaskRetirementState> RetireAsync(
        bool explicitlyConfirmed,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await ReadStateCoreAsync(cancellationToken).ConfigureAwait(false);
            if (existing?.IsRetired == true)
            {
                return existing;
            }

            var batch = await _tasks.RetireKnownTasksAsync(explicitlyConfirmed, cancellationToken).ConfigureAwait(false);
            var now = _clock.UtcNow;
            var allRetired = batch.Results
                .Where(static result => result.Existed)
                .All(static result => result.Disabled && result.SafeErrorCode is null);
            var state = new LegacyTaskRetirementState(
                SchemaVersion: 1,
                CapturedAtUtc: now,
                RetiredAtUtc: allRetired ? now : null,
                RestoredAtUtc: null,
                batch.Snapshots,
                batch.Results,
                []);
            await WriteStateCoreAsync(state, cancellationToken).ConfigureAwait(false);
            return state;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LegacyTaskRetirementState> RestoreAsync(
        bool explicitlyConfirmed,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadStateCoreAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("No legacy task retirement snapshot is available.");
            if (!state.IsRetired)
            {
                return state;
            }

            var restore = await _tasks.RestoreKnownTasksAsync(
                state.Snapshots,
                explicitlyConfirmed,
                cancellationToken).ConfigureAwait(false);
            var allRestored = restore.All(static result => result.Restored && result.SafeErrorCode is null);
            var updated = state with
            {
                RestoredAtUtc = allRestored ? _clock.UtcNow : null,
                RestoreResults = restore,
            };
            await WriteStateCoreAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<LegacyTaskRetirementState?> ReadStateCoreAsync(CancellationToken cancellationToken)
    {
        var path = StatePath;
        if (!File.Exists(path))
        {
            return null;
        }

        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > MaximumStateBytes)
        {
            _logger.LogWarning("Legacy task retirement state has an invalid size and will not be trusted.");
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                8192,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var state = await JsonSerializer.DeserializeAsync<LegacyTaskRetirementState>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            return state is { SchemaVersion: 1 } ? state : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(exception, "Legacy task retirement state could not be read.");
            return null;
        }
    }

    private Task WriteStateCoreAsync(
        LegacyTaskRetirementState state,
        CancellationToken cancellationToken) =>
        AtomicFileWriter.WriteAsync(
            StatePath,
            async (stream, token) =>
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, token).ConfigureAwait(false),
            cancellationToken,
            retainBackup: true);
}
