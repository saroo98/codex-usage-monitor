using System.Diagnostics;
using System.Xml.Linq;

namespace CodexUsageMonitor.Migration.Tasks;

public sealed record LegacyTaskResult(string Name, bool Existed, bool Removed, string? SafeErrorCode);

public sealed record LegacyTaskSnapshot(
    string Name,
    bool Existed,
    bool WasEnabled,
    string? DefinitionXml,
    string? SafeErrorCode);

public sealed record LegacyTaskRetirementResult(
    string Name,
    bool Existed,
    bool WasEnabled,
    bool Disabled,
    string? SafeErrorCode);

public sealed record LegacyTaskRestoreResult(
    string Name,
    bool Existed,
    bool Restored,
    string? SafeErrorCode);

public sealed record LegacyTaskRetirementBatch(
    IReadOnlyList<LegacyTaskSnapshot> Snapshots,
    IReadOnlyList<LegacyTaskRetirementResult> Results);

public sealed record LegacyTaskCommandResult(int ExitCode, string StandardOutput, string StandardError);

public interface ILegacyTaskCommandRunner
{
    Task<LegacyTaskCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

public interface ILegacyScheduledTaskController
{
    Task<IReadOnlyList<LegacyTaskSnapshot>> CaptureKnownTasksAsync(CancellationToken cancellationToken);

    Task<LegacyTaskRetirementBatch> RetireKnownTasksAsync(bool explicitlyConfirmed, CancellationToken cancellationToken);

    Task<IReadOnlyList<LegacyTaskRestoreResult>> RestoreKnownTasksAsync(
        IReadOnlyList<LegacyTaskSnapshot> snapshots,
        bool explicitlyConfirmed,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<LegacyTaskResult>> RemoveKnownTasksAsync(bool explicitlyConfirmed, CancellationToken cancellationToken);
}

public sealed class LegacyScheduledTaskController : ILegacyScheduledTaskController
{
    private static readonly string[] KnownTaskNames =
    [
        "Codex Usage Notifier",
        "Codex Usage Notifier UI",
        "Codex Usage Notifier Watchdog",
    ];

    private readonly ILegacyTaskCommandRunner _runner;

    public LegacyScheduledTaskController(ILegacyTaskCommandRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public async Task<IReadOnlyList<LegacyTaskSnapshot>> CaptureKnownTasksAsync(CancellationToken cancellationToken)
    {
        var snapshots = new List<LegacyTaskSnapshot>(KnownTaskNames.Length);
        foreach (var taskName in KnownTaskNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = await _runner.RunAsync(["/Query", "/TN", taskName, "/XML"], cancellationToken).ConfigureAwait(false);
            if (query.ExitCode != 0)
            {
                snapshots.Add(new LegacyTaskSnapshot(taskName, false, false, null, null));
                continue;
            }

            try
            {
                var document = XDocument.Parse(query.StandardOutput, LoadOptions.PreserveWhitespace);
                var enabled = document.Descendants()
                    .FirstOrDefault(static element => element.Name.LocalName.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
                    ?.Value;
                snapshots.Add(new LegacyTaskSnapshot(
                    taskName,
                    true,
                    !string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase),
                    query.StandardOutput,
                    null));
            }
            catch (Exception exception) when (exception is System.Xml.XmlException or InvalidOperationException)
            {
                snapshots.Add(new LegacyTaskSnapshot(taskName, true, false, null, "migration.task_definition_invalid"));
            }
        }

        return snapshots.AsReadOnly();
    }

    public async Task<LegacyTaskRetirementBatch> RetireKnownTasksAsync(
        bool explicitlyConfirmed,
        CancellationToken cancellationToken)
    {
        RequireConfirmation(explicitlyConfirmed, "Legacy task retirement");
        var snapshots = await CaptureKnownTasksAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<LegacyTaskRetirementResult>(snapshots.Count);
        foreach (var snapshot in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!snapshot.Existed)
            {
                results.Add(new LegacyTaskRetirementResult(snapshot.Name, false, false, false, null));
                continue;
            }

            if (snapshot.SafeErrorCode is not null)
            {
                results.Add(new LegacyTaskRetirementResult(
                    snapshot.Name,
                    true,
                    snapshot.WasEnabled,
                    false,
                    snapshot.SafeErrorCode));
                continue;
            }

            _ = await _runner.RunAsync(["/End", "/TN", snapshot.Name], cancellationToken).ConfigureAwait(false);
            var disable = await _runner.RunAsync(
                ["/Change", "/TN", snapshot.Name, "/Disable"],
                cancellationToken).ConfigureAwait(false);
            results.Add(new LegacyTaskRetirementResult(
                snapshot.Name,
                true,
                snapshot.WasEnabled,
                disable.ExitCode == 0,
                disable.ExitCode == 0 ? null : "migration.task_disable_failed"));
        }

        return new LegacyTaskRetirementBatch(snapshots, results.AsReadOnly());
    }

    public async Task<IReadOnlyList<LegacyTaskRestoreResult>> RestoreKnownTasksAsync(
        IReadOnlyList<LegacyTaskSnapshot> snapshots,
        bool explicitlyConfirmed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        RequireConfirmation(explicitlyConfirmed, "Legacy task restoration");
        var known = snapshots
            .Where(static snapshot => KnownTaskNames.Contains(snapshot.Name, StringComparer.OrdinalIgnoreCase))
            .GroupBy(static snapshot => snapshot.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();
        var results = new List<LegacyTaskRestoreResult>(known.Length);
        foreach (var snapshot in known)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!snapshot.Existed)
            {
                results.Add(new LegacyTaskRestoreResult(snapshot.Name, false, true, null));
                continue;
            }

            var query = await _runner.RunAsync(["/Query", "/TN", snapshot.Name], cancellationToken).ConfigureAwait(false);
            if (query.ExitCode != 0)
            {
                results.Add(new LegacyTaskRestoreResult(snapshot.Name, true, false, "migration.task_missing"));
                continue;
            }

            var restore = await _runner.RunAsync(
                ["/Change", "/TN", snapshot.Name, snapshot.WasEnabled ? "/Enable" : "/Disable"],
                cancellationToken).ConfigureAwait(false);
            results.Add(new LegacyTaskRestoreResult(
                snapshot.Name,
                true,
                restore.ExitCode == 0,
                restore.ExitCode == 0 ? null : "migration.task_restore_failed"));
        }

        return results.AsReadOnly();
    }

    public async Task<IReadOnlyList<LegacyTaskResult>> RemoveKnownTasksAsync(
        bool explicitlyConfirmed,
        CancellationToken cancellationToken)
    {
        RequireConfirmation(explicitlyConfirmed, "Legacy task removal");
        var results = new List<LegacyTaskResult>(KnownTaskNames.Length);
        foreach (var taskName in KnownTaskNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = await _runner.RunAsync(["/Query", "/TN", taskName], cancellationToken).ConfigureAwait(false);
            if (query.ExitCode != 0)
            {
                results.Add(new LegacyTaskResult(taskName, false, false, null));
                continue;
            }

            _ = await _runner.RunAsync(["/End", "/TN", taskName], cancellationToken).ConfigureAwait(false);
            var delete = await _runner.RunAsync(["/Delete", "/TN", taskName, "/F"], cancellationToken).ConfigureAwait(false);
            results.Add(new LegacyTaskResult(
                taskName,
                true,
                delete.ExitCode == 0,
                delete.ExitCode == 0 ? null : "migration.task_delete_failed"));
        }

        return results.AsReadOnly();
    }

    private static void RequireConfirmation(bool explicitlyConfirmed, string action)
    {
        if (!explicitlyConfirmed)
        {
            throw new InvalidOperationException($"{action} requires explicit confirmation.");
        }
    }
}

public sealed class SchtasksCommandRunner : ILegacyTaskCommandRunner
{
    private const int MaximumCapturedCharacters = 1_048_576;

    public async Task<LegacyTaskCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException("schtasks.exe could not be started.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = Bound(await outputTask.ConfigureAwait(false));
        var error = Bound(await errorTask.ConfigureAwait(false));
        return new LegacyTaskCommandResult(process.ExitCode, output, error);
    }

    private static string Bound(string value) =>
        value.Length <= MaximumCapturedCharacters ? value : value[..MaximumCapturedCharacters];
}
