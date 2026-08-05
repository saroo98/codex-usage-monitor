using CodexUsageMonitor.Migration.Tasks;

namespace CodexUsageMonitor.MigrationTests;

[TestClass]
public sealed class LegacyScheduledTaskControllerTests
{
    private const string EnabledTaskXml = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><Settings><Enabled>true</Enabled></Settings></Task>
        """;

    private const string DisabledTaskXml = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><Settings><Enabled>false</Enabled></Settings></Task>
        """;

    [TestMethod]
    public async Task CaptureKnownTasksParsesEnabledStateAndTreatsMissingTasksAsAbsent()
    {
        var runner = new FakeTaskCommandRunner((arguments, _) =>
        {
            var name = arguments[2];
            return name switch
            {
                "Codex Usage Notifier" => Success(EnabledTaskXml),
                "Codex Usage Notifier UI" => Success(DisabledTaskXml),
                _ => Failure(),
            };
        });

        var snapshots = await new LegacyScheduledTaskController(runner)
            .CaptureKnownTasksAsync(CancellationToken.None);

        Assert.HasCount(3, snapshots);
        Assert.IsTrue(snapshots[0].Existed);
        Assert.IsTrue(snapshots[0].WasEnabled);
        Assert.IsTrue(snapshots[1].Existed);
        Assert.IsFalse(snapshots[1].WasEnabled);
        Assert.IsFalse(snapshots[2].Existed);
    }

    [TestMethod]
    public async Task RetireKnownTasksEndsAndDisablesOnlyExistingTasks()
    {
        var commands = new List<string>();
        var runner = new FakeTaskCommandRunner((arguments, _) =>
        {
            commands.Add(string.Join(' ', arguments));
            if (arguments.Contains("/XML", StringComparer.OrdinalIgnoreCase))
            {
                return arguments[2] == "Codex Usage Notifier" ? Success(EnabledTaskXml) : Failure();
            }

            return Success(string.Empty);
        });

        var batch = await new LegacyScheduledTaskController(runner)
            .RetireKnownTasksAsync(explicitlyConfirmed: true, CancellationToken.None);

        Assert.HasCount(3, batch.Results);
        Assert.IsTrue(batch.Results[0].Disabled);
        Assert.IsTrue(commands.Contains("/End /TN Codex Usage Notifier", StringComparer.Ordinal));
        Assert.IsTrue(commands.Contains("/Change /TN Codex Usage Notifier /Disable", StringComparer.Ordinal));
        Assert.AreEqual(5, commands.Count);
    }

    [TestMethod]
    public async Task RestoreKnownTasksReturnsEachTaskToItsCapturedEnabledState()
    {
        var commands = new List<string>();
        var runner = new FakeTaskCommandRunner((arguments, _) =>
        {
            commands.Add(string.Join(' ', arguments));
            return Success(string.Empty);
        });
        var snapshots = new[]
        {
            new LegacyTaskSnapshot("Codex Usage Notifier", true, true, EnabledTaskXml, null),
            new LegacyTaskSnapshot("Codex Usage Notifier UI", true, false, DisabledTaskXml, null),
            new LegacyTaskSnapshot("Unrelated task", true, true, EnabledTaskXml, null),
        };

        var results = await new LegacyScheduledTaskController(runner)
            .RestoreKnownTasksAsync(snapshots, explicitlyConfirmed: true, CancellationToken.None);

        Assert.HasCount(2, results);
        Assert.IsTrue(results.All(static result => result.Restored));
        Assert.IsTrue(commands.Contains("/Change /TN Codex Usage Notifier /Enable", StringComparer.Ordinal));
        Assert.IsTrue(commands.Contains("/Change /TN Codex Usage Notifier UI /Disable", StringComparer.Ordinal));
        Assert.IsFalse(commands.Any(static command => command.Contains("Unrelated task", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task DestructiveAndStateChangingOperationsRequireExplicitConfirmation()
    {
        var controller = new LegacyScheduledTaskController(new FakeTaskCommandRunner((_, _) => Success(string.Empty)));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.RetireKnownTasksAsync(explicitlyConfirmed: false, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.RestoreKnownTasksAsync([], explicitlyConfirmed: false, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.RemoveKnownTasksAsync(explicitlyConfirmed: false, CancellationToken.None));
    }

    private static LegacyTaskCommandResult Success(string output) => new(0, output, string.Empty);

    private static LegacyTaskCommandResult Failure() => new(1, string.Empty, "not found");

    private sealed class FakeTaskCommandRunner(
        Func<IReadOnlyList<string>, CancellationToken, LegacyTaskCommandResult> handler) : ILegacyTaskCommandRunner
    {
        public Task<LegacyTaskCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(handler(arguments, cancellationToken));
        }
    }
}
