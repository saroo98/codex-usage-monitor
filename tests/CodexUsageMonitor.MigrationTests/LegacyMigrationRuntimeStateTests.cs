using CodexUsageMonitor.Migration.Execution;

namespace CodexUsageMonitor.MigrationTests;

[TestClass]
public sealed class LegacyMigrationRuntimeStateTests
{
    [TestMethod]
    public void StatePublishesMigrationAndRetirementChanges()
    {
        var state = new LegacyMigrationRuntimeState();
        var changes = 0;
        state.Changed += (_, _) => changes++;
        var migration = new LegacyMigrationResult(
            true,
            true,
            "5.0.0",
            "backup",
            "backup.zip",
            "abc",
            [],
            [],
            null);
        var retirement = new LegacyTaskRetirementState(
            1,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            [],
            [],
            []);

        state.SetMigration(migration);
        state.SetRetirement(retirement);

        Assert.AreSame(migration, state.Migration);
        Assert.AreSame(retirement, state.Retirement);
        Assert.AreEqual(2, changes);
    }
}
