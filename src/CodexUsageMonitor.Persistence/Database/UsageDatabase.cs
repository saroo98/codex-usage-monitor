using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.Persistence.Database;

public sealed class UsageDatabase
{
    public const int CurrentSchemaVersion = 2;
    private readonly SqliteConnectionFactory _factory;
    private readonly ILogger<UsageDatabase> _logger;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private int _initialized;

    public UsageDatabase(SqliteConnectionFactory factory, ILogger<UsageDatabase> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public SqliteConnectionFactory Connections => _factory;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _initialized) != 0)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _initialized) != 0)
            {
                return;
            }

            await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA auto_vacuum=INCREMENTAL;", cancellationToken).ConfigureAwait(false);
            var version = await ReadSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);
            if (version > CurrentSchemaVersion)
            {
                throw new InvalidDataException("Usage database was created by a newer application version.");
            }

            if (version < 1)
            {
                await ApplyVersion1Async(connection, cancellationToken).ConfigureAwait(false);
                version = 1;
            }

            if (version < 2)
            {
                await ApplyVersion2Async(connection, cancellationToken).ConfigureAwait(false);
            }

            Volatile.Write(ref _initialized, 1);
        }
        catch (SqliteException exception)
        {
            _logger.LogError(exception, "SQLite initialization failed with code {SqliteCode}.", exception.SqliteErrorCode);
            throw;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async Task<string> IntegrityCheckAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToString(result, System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
    }

    public async Task MaintainAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA wal_checkpoint(PASSIVE); PRAGMA incremental_vacuum(256); PRAGMA optimize;", cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyVersion1Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            BEGIN IMMEDIATE;
            CREATE TABLE IF NOT EXISTS usage_samples (
                profile_id TEXT NOT NULL,
                account_key TEXT NOT NULL,
                limit_id TEXT NOT NULL,
                observed_at_utc INTEGER NOT NULL,
                kind INTEGER NOT NULL,
                label TEXT NOT NULL,
                remaining_percent REAL NOT NULL CHECK (remaining_percent >= 0 AND remaining_percent <= 100),
                used_percent REAL NOT NULL CHECK (used_percent >= 0 AND used_percent <= 100),
                resets_at_utc INTEGER NULL,
                PRIMARY KEY (profile_id, account_key, limit_id, observed_at_utc)
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS ix_usage_samples_lookup
                ON usage_samples(profile_id, account_key, limit_id, observed_at_utc DESC);
            CREATE TABLE IF NOT EXISTS daily_usage (
                profile_id TEXT NOT NULL,
                account_key TEXT NOT NULL,
                limit_id TEXT NOT NULL,
                day_utc INTEGER NOT NULL,
                min_remaining REAL NOT NULL,
                max_remaining REAL NOT NULL,
                first_remaining REAL NOT NULL,
                last_remaining REAL NOT NULL,
                sample_count INTEGER NOT NULL,
                PRIMARY KEY (profile_id, account_key, limit_id, day_utc)
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS notification_receipts (
                identity TEXT PRIMARY KEY,
                profile_id TEXT NOT NULL,
                account_key TEXT NOT NULL,
                delivered_at_utc INTEGER NOT NULL,
                expires_at_utc INTEGER NOT NULL
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS ix_notification_receipts_expiry
                ON notification_receipts(expires_at_utc);
            CREATE TABLE IF NOT EXISTS deferred_notifications (
                identity TEXT PRIMARY KEY,
                profile_id TEXT NOT NULL,
                account_key TEXT NOT NULL,
                limit_id TEXT NOT NULL,
                event_type INTEGER NOT NULL,
                transition_key TEXT NOT NULL,
                deferred_at_utc INTEGER NOT NULL,
                deliver_after_utc INTEGER NOT NULL,
                expires_at_utc INTEGER NOT NULL,
                payload_code TEXT NOT NULL,
                priority INTEGER NOT NULL
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS ix_deferred_notifications_due
                ON deferred_notifications(deliver_after_utc, expires_at_utc);
            CREATE TABLE IF NOT EXISTS email_outbox (
                id TEXT PRIMARY KEY,
                deduplication_key TEXT NOT NULL UNIQUE,
                profile_id TEXT NOT NULL,
                account_key TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                queued_at_utc INTEGER NOT NULL,
                available_at_utc INTEGER NOT NULL,
                expires_at_utc INTEGER NOT NULL,
                attempt_count INTEGER NOT NULL DEFAULT 0,
                last_error_code TEXT NULL,
                leased_until_utc INTEGER NULL
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS ix_email_outbox_due
                ON email_outbox(available_at_utc, expires_at_utc, leased_until_utc);
            CREATE TABLE IF NOT EXISTS reset_redemptions (
                idempotency_key TEXT PRIMARY KEY,
                profile_id TEXT NOT NULL,
                account_key TEXT NOT NULL,
                reset_credit_id TEXT NOT NULL,
                started_at_utc INTEGER NOT NULL,
                completed_at_utc INTEGER NULL,
                outcome_code TEXT NULL
            ) WITHOUT ROWID;
            PRAGMA user_version=1;
            COMMIT;
            """;
        await ExecuteAsync(connection, sql, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyVersion2Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            BEGIN IMMEDIATE;
            ALTER TABLE email_outbox ADD COLUMN terminal_at_utc INTEGER NULL;
            ALTER TABLE email_outbox ADD COLUMN terminal_reason TEXT NULL;
            CREATE TABLE IF NOT EXISTS diagnostic_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                occurred_at_utc INTEGER NOT NULL,
                level INTEGER NOT NULL,
                event_code TEXT NOT NULL,
                safe_detail TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_diagnostic_events_time ON diagnostic_events(occurred_at_utc DESC);
            PRAGMA user_version=2;
            COMMIT;
            """;
        await ExecuteAsync(connection, sql, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadSchemaVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
