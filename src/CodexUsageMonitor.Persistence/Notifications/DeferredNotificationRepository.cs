using CodexUsageMonitor.Core.Notifications;
using CodexUsageMonitor.Persistence.Database;

namespace CodexUsageMonitor.Persistence.Notifications;

public sealed class DeferredNotificationRepository
{
    private readonly UsageDatabase _database;

    public DeferredNotificationRepository(UsageDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task UpsertAsync(DeferredNotification item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _database.Connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO deferred_notifications
            (identity, profile_id, account_key, limit_id, event_type, transition_key,
             deferred_at_utc, deliver_after_utc, expires_at_utc, payload_code, priority)
            VALUES ($identity, $profile, $account, $limit, $event, $transition,
                    $deferred, $deliver, $expires, $payload, $priority)
            ON CONFLICT(identity) DO UPDATE SET
                deliver_after_utc=MIN(deliver_after_utc, excluded.deliver_after_utc),
                expires_at_utc=MAX(expires_at_utc, excluded.expires_at_utc),
                payload_code=excluded.payload_code,
                priority=MAX(priority, excluded.priority);
            """;
        Bind(command, item);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DateTimeOffset?> GetNextDeliverAtAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _database.Connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MIN(deliver_after_utc)
            FROM deferred_notifications
            WHERE expires_at_utc > $now;
            """;
        command.Parameters.AddWithValue("$now", nowUtc.ToUnixTimeSeconds());
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull
            ? null
            : DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
    }

    public async Task<IReadOnlyList<DeferredNotification>> ReadDueAsync(
        DateTimeOffset nowUtc,
        int maximum,
        CancellationToken cancellationToken)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _database.Connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT profile_id, account_key, limit_id, event_type, transition_key,
                   deferred_at_utc, deliver_after_utc, expires_at_utc, payload_code, priority
            FROM deferred_notifications
            WHERE deliver_after_utc <= $now AND expires_at_utc > $now
            ORDER BY priority DESC, deliver_after_utc ASC
            LIMIT $maximum;
            """;
        command.Parameters.AddWithValue("$now", nowUtc.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$maximum", Math.Clamp(maximum, 1, 100));
        var items = new List<DeferredNotification>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var profileId = Guid.Parse(reader.GetString(0));
            var account = reader.GetString(1);
            var limit = reader.GetString(2);
            var eventType = (NotificationEventType)reader.GetInt32(3);
            var transition = reader.GetString(4);
            items.Add(new DeferredNotification(
                new NotificationIdentity(profileId, account, limit, eventType, transition),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6)),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(7)),
                reader.GetString(8),
                reader.GetInt32(9)));
        }

        return items;
    }

    public async Task DeleteAsync(NotificationIdentity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _database.Connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM deferred_notifications WHERE identity=$identity;";
        command.Parameters.AddWithValue("$identity", identity.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CleanupExpiredAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _database.Connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM deferred_notifications WHERE expires_at_utc <= $now;";
        command.Parameters.AddWithValue("$now", nowUtc.ToUnixTimeSeconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Bind(Microsoft.Data.Sqlite.SqliteCommand command, DeferredNotification item)
    {
        command.Parameters.AddWithValue("$identity", item.Identity.Value);
        command.Parameters.AddWithValue("$profile", item.Identity.ProfileId.ToString("D"));
        command.Parameters.AddWithValue("$account", item.Identity.AccountStorageKey);
        command.Parameters.AddWithValue("$limit", item.Identity.LimitIdentity);
        command.Parameters.AddWithValue("$event", (int)item.Identity.EventType);
        command.Parameters.AddWithValue("$transition", item.Identity.TransitionKey);
        command.Parameters.AddWithValue("$deferred", item.DeferredAtUtc.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$deliver", item.DeliverAfterUtc.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$expires", item.ExpiresAtUtc.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$payload", item.PayloadCode);
        command.Parameters.AddWithValue("$priority", item.Priority);
    }
}
