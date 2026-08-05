using CodexUsageMonitor.Persistence.Database;

namespace CodexUsageMonitor.Persistence.ResetCredits;

public sealed record ResetRedemption(
    Guid IdempotencyKey,
    Guid ProfileId,
    string AccountKey,
    string ResetCreditId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? OutcomeCode);

public sealed class ResetRedemptionRepository
{
    private readonly UsageDatabase _database;

    public ResetRedemptionRepository(UsageDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<bool> TryBeginAsync(ResetRedemption redemption, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(redemption);
        if (redemption.IdempotencyKey == Guid.Empty || redemption.ProfileId == Guid.Empty)
        {
            throw new ArgumentException("Redemption identity cannot be empty.", nameof(redemption));
        }

        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _database.Connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO reset_redemptions
            (idempotency_key, profile_id, account_key, reset_credit_id, started_at_utc, completed_at_utc, outcome_code)
            VALUES ($key, $profile, $account, $credit, $started, NULL, NULL);
            """;
        command.Parameters.AddWithValue("$key", redemption.IdempotencyKey.ToString("D"));
        command.Parameters.AddWithValue("$profile", redemption.ProfileId.ToString("D"));
        command.Parameters.AddWithValue("$account", redemption.AccountKey);
        command.Parameters.AddWithValue("$credit", redemption.ResetCreditId);
        command.Parameters.AddWithValue("$started", redemption.StartedAtUtc.ToUnixTimeSeconds());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<ResetRedemption?> ReadAsync(Guid idempotencyKey, CancellationToken cancellationToken)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _database.Connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT profile_id, account_key, reset_credit_id, started_at_utc, completed_at_utc, outcome_code
            FROM reset_redemptions WHERE idempotency_key=$key;
            """;
        command.Parameters.AddWithValue("$key", idempotencyKey.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ResetRedemption(
            idempotencyKey,
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)),
            reader.IsDBNull(4) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    public async Task CompleteAsync(
        Guid idempotencyKey,
        DateTimeOffset completedAtUtc,
        string outcomeCode,
        CancellationToken cancellationToken)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _database.Connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reset_redemptions
            SET completed_at_utc=COALESCE(completed_at_utc, $completed),
                outcome_code=COALESCE(outcome_code, $outcome)
            WHERE idempotency_key=$key;
            """;
        command.Parameters.AddWithValue("$completed", completedAtUtc.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$outcome", outcomeCode);
        command.Parameters.AddWithValue("$key", idempotencyKey.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
