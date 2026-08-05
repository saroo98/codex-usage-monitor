using CodexUsageMonitor.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace CodexUsageMonitor.Persistence.Outbox;

public sealed record EmailOutboxStatistics(
    int PendingCount,
    int TerminalCount,
    DateTimeOffset? NextAvailableAtUtc,
    string? LastSafeErrorCode);

public sealed class EmailOutboxRepository
{
    private readonly UsageDatabase _database;

    public EmailOutboxRepository(UsageDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<bool> TryEnqueueAsync(EmailOutboxItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _database.Connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO email_outbox
            (id, deduplication_key, profile_id, account_key, payload_json, queued_at_utc,
             available_at_utc, expires_at_utc, attempt_count, last_error_code, leased_until_utc,
             terminal_at_utc, terminal_reason)
            VALUES ($id, $dedup, $profile, $account, $payload, $queued,
                    $available, $expires, $attempts, $error, NULL, NULL, NULL);
            """;
        command.Parameters.AddWithValue("$id", item.Id.ToString("D"));
        command.Parameters.AddWithValue("$dedup", item.DeduplicationKey);
        command.Parameters.AddWithValue("$profile", item.ProfileId.ToString("D"));
        command.Parameters.AddWithValue("$account", item.AccountKey);
        command.Parameters.AddWithValue("$payload", item.PayloadJson);
        command.Parameters.AddWithValue("$queued", item.QueuedAtUtc.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$available", item.AvailableAtUtc.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$expires", item.ExpiresAtUtc.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$attempts", item.AttemptCount);
        command.Parameters.AddWithValue("$error", item.LastErrorCode is null ? DBNull.Value : item.LastErrorCode);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<EmailOutboxStatistics> GetStatisticsAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _database.Connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                SUM(CASE WHEN terminal_at_utc IS NULL AND expires_at_utc > $now THEN 1 ELSE 0 END),
                SUM(CASE WHEN terminal_at_utc IS NOT NULL THEN 1 ELSE 0 END),
                MIN(CASE
                    WHEN terminal_at_utc IS NULL AND expires_at_utc > $now THEN
                        CASE
                            WHEN leased_until_utc IS NOT NULL AND leased_until_utc > $now
                            THEN MAX(available_at_utc, leased_until_utc)
                            ELSE available_at_utc
                        END
                    ELSE NULL
                END),
                (SELECT COALESCE(last_error_code, terminal_reason)
                   FROM email_outbox
                  WHERE last_error_code IS NOT NULL OR terminal_reason IS NOT NULL
                  ORDER BY COALESCE(terminal_at_utc, queued_at_utc) DESC
                  LIMIT 1)
            FROM email_outbox;
            """;
        command.Parameters.AddWithValue("$now", nowUtc.ToUnixTimeSeconds());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new EmailOutboxStatistics(0, 0, null, null);
        }

        return new EmailOutboxStatistics(
            reader.IsDBNull(0) ? 0 : checked((int)reader.GetInt64(0)),
            reader.IsDBNull(1) ? 0 : checked((int)reader.GetInt64(1)),
            reader.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    public async Task<DateTimeOffset?> GetNextPendingAtAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _database.Connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MIN(
                CASE
                    WHEN leased_until_utc IS NOT NULL AND leased_until_utc > $now
                    THEN MAX(available_at_utc, leased_until_utc)
                    ELSE available_at_utc
                END)
            FROM email_outbox
            WHERE terminal_at_utc IS NULL
              AND expires_at_utc > $now;
            """;
        command.Parameters.AddWithValue("$now", nowUtc.ToUnixTimeSeconds());
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull
            ? null
            : DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
    }

    public async Task<EmailOutboxItem?> TryLeaseNextAsync(
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _database.Connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT id, deduplication_key, profile_id, account_key, payload_json, queued_at_utc,
                   available_at_utc, expires_at_utc, attempt_count, last_error_code, leased_until_utc,
                   terminal_at_utc, terminal_reason
            FROM email_outbox
            WHERE terminal_at_utc IS NULL
              AND available_at_utc <= $now
              AND expires_at_utc > $now
              AND (leased_until_utc IS NULL OR leased_until_utc <= $now)
            ORDER BY available_at_utc ASC, queued_at_utc ASC
            LIMIT 1;
            """;
        select.Parameters.AddWithValue("$now", nowUtc.ToUnixTimeSeconds());
        EmailOutboxItem? item = null;
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                item = Read(reader);
            }
        }

        if (item is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var leaseUntil = nowUtc + leaseDuration;
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE email_outbox SET leased_until_utc=$lease
            WHERE id=$id AND (leased_until_utc IS NULL OR leased_until_utc <= $now);
            """;
        update.Parameters.AddWithValue("$lease", leaseUntil.ToUnixTimeSeconds());
        update.Parameters.AddWithValue("$id", item.Id.ToString("D"));
        update.Parameters.AddWithValue("$now", nowUtc.ToUnixTimeSeconds());
        var updated = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated == 1 ? item with { LeasedUntilUtc = leaseUntil } : null;
    }

    public async Task CompleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await DeleteAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task RetryAsync(
        Guid id,
        int attemptCount,
        DateTimeOffset availableAtUtc,
        string safeErrorCode,
        CancellationToken cancellationToken)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _database.Connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE email_outbox
            SET attempt_count=$attempts, available_at_utc=$available, last_error_code=$error, leased_until_utc=NULL
            WHERE id=$id AND terminal_at_utc IS NULL;
            """;
        command.Parameters.AddWithValue("$attempts", Math.Max(0, attemptCount));
        command.Parameters.AddWithValue("$available", availableAtUtc.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$error", safeErrorCode);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkTerminalAsync(
        Guid id,
        DateTimeOffset terminalAtUtc,
        string reason,
        CancellationToken cancellationToken)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _database.Connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE email_outbox
            SET terminal_at_utc=$terminal, terminal_reason=$reason, leased_until_utc=NULL
            WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$terminal", terminalAtUtc.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CleanupAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _database.Connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM email_outbox
            WHERE expires_at_utc <= $now OR (terminal_at_utc IS NOT NULL AND terminal_at_utc <= $terminalCutoff);
            """;
        command.Parameters.AddWithValue("$now", nowUtc.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$terminalCutoff", nowUtc.AddDays(-14).ToUnixTimeSeconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _database.Connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM email_outbox WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static EmailOutboxItem Read(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        Guid.Parse(reader.GetString(2)),
        reader.GetString(3),
        reader.GetString(4),
        DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)),
        DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6)),
        DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(7)),
        reader.GetInt32(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(10)),
        reader.IsDBNull(11) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(11)),
        reader.IsDBNull(12) ? null : reader.GetString(12));
}
