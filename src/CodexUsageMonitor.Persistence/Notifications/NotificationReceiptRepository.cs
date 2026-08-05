using CodexUsageMonitor.Core.Notifications;
using CodexUsageMonitor.Persistence.Database;

namespace CodexUsageMonitor.Persistence.Notifications;

public sealed class NotificationReceiptRepository
{
    private readonly UsageDatabase _database;

    public NotificationReceiptRepository(UsageDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<bool> TryReserveAsync(
        NotificationIdentity identity,
        DateTimeOffset deliveredAtUtc,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _database.Connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO notification_receipts
            (identity, profile_id, account_key, delivered_at_utc, expires_at_utc)
            VALUES ($identity, $profile, $account, $delivered, $expires);
            """;
        command.Parameters.AddWithValue("$identity", identity.Value);
        command.Parameters.AddWithValue("$profile", identity.ProfileId.ToString("D"));
        command.Parameters.AddWithValue("$account", identity.AccountStorageKey);
        command.Parameters.AddWithValue("$delivered", deliveredAtUtc.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$expires", expiresAtUtc.ToUnixTimeSeconds());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }


    public async Task<bool> ExistsAsync(NotificationIdentity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _database.Connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM notification_receipts WHERE identity=$identity);";
        command.Parameters.AddWithValue("$identity", identity.Value);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    public async Task ReleaseAsync(NotificationIdentity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _database.Connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM notification_receipts WHERE identity=$identity;";
        command.Parameters.AddWithValue("$identity", identity.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CleanupAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _database.Connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM notification_receipts WHERE expires_at_utc <= $now;";
        command.Parameters.AddWithValue("$now", nowUtc.ToUnixTimeSeconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
