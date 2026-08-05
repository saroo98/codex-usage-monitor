namespace CodexUsageMonitor.Migration.Execution;

public sealed class LegacyMigrationRuntimeState
{
    private readonly object _gate = new();
    private LegacyMigrationResult? _migration;
    private LegacyTaskRetirementState? _retirement;

    public event EventHandler? Changed;

    public LegacyMigrationResult? Migration
    {
        get
        {
            lock (_gate)
            {
                return _migration;
            }
        }
    }

    public LegacyTaskRetirementState? Retirement
    {
        get
        {
            lock (_gate)
            {
                return _retirement;
            }
        }
    }

    public void SetMigration(LegacyMigrationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            _migration = result;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetRetirement(LegacyTaskRetirementState? state)
    {
        lock (_gate)
        {
            _retirement = state;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
