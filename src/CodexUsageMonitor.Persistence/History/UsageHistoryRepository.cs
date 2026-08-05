using CodexUsageMonitor.Application.Monitoring;
using CodexUsageMonitor.Core.Usage;
using CodexUsageMonitor.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace CodexUsageMonitor.Persistence.History;

public sealed class UsageHistoryRepository : IUsageHistoryWriter
{
    private readonly UsageDatabase _database;

    public UsageHistoryRepository(UsageDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task RecordAsync(UsageSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _database.Connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var limit in snapshot.Limits)
        {
            await InsertSampleAsync(connection, transaction, snapshot, limit, cancellationToken).ConfigureAwait(false);
            await UpsertDailyAsync(connection, transaction, snapshot, limit, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    async Task IUsageHistoryWriter.RecordAsync(UsageSnapshot snapshot, CancellationToken cancellationToken)
    {
        try
        {
            await RecordAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw new UsageHistoryWriteException("Usage history persistence failed.", exception);
        }
    }

    public async Task<IReadOnlyList<HistoryPoint>> ReadAsync(
        Guid profileId,
        string accountKey,
        string limitIdentity,
        DateTimeOffset fromUtc,
        int maximumPoints,
        CancellationToken cancellationToken)
    {
        ValidateScope(profileId, accountKey, limitIdentity);
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _database.Connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT observed_at_utc, remaining_percent, used_percent, resets_at_utc
            FROM usage_samples
            WHERE profile_id=$profile AND account_key=$account AND limit_id=$limit AND observed_at_utc >= $from
            ORDER BY observed_at_utc ASC
            LIMIT $limitCount;
            """;
        command.Parameters.AddWithValue("$profile", profileId.ToString("D"));
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$limit", limitIdentity);
        command.Parameters.AddWithValue("$from", fromUtc.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$limitCount", Math.Clamp(maximumPoints, 1, 20_000));
        var points = new List<HistoryPoint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            points.Add(new HistoryPoint(
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)),
                Convert.ToDecimal(reader.GetDouble(1), System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetDouble(2), System.Globalization.CultureInfo.InvariantCulture),
                reader.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3))));
        }

        return points;
    }

    public async Task<IReadOnlyList<DailyUsagePoint>> ReadDailyAsync(
        Guid profileId,
        string accountKey,
        string limitIdentity,
        DateOnly fromDayUtc,
        int maximumDays,
        CancellationToken cancellationToken)
    {
        ValidateScope(profileId, accountKey, limitIdentity);
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _database.Connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT day_utc, min_remaining, max_remaining, first_remaining, last_remaining, sample_count
            FROM daily_usage
            WHERE profile_id=$profile AND account_key=$account AND limit_id=$limit AND day_utc >= $day
            ORDER BY day_utc ASC
            LIMIT $days;
            """;
        command.Parameters.AddWithValue("$profile", profileId.ToString("D"));
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$limit", limitIdentity);
        command.Parameters.AddWithValue("$day", fromDayUtc.DayNumber);
        command.Parameters.AddWithValue("$days", Math.Clamp(maximumDays, 1, 366));
        var points = new List<DailyUsagePoint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            points.Add(new DailyUsagePoint(
                DateOnly.FromDayNumber(reader.GetInt32(0)),
                ToDecimal(reader.GetDouble(1)),
                ToDecimal(reader.GetDouble(2)),
                ToDecimal(reader.GetDouble(3)),
                ToDecimal(reader.GetDouble(4)),
                reader.GetInt32(5)));
        }

        return points;
    }

    public async Task PruneAsync(int retentionDays, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var cutoff = nowUtc.AddDays(-Math.Clamp(retentionDays, 7, 365));
        await using var connection = await _database.Connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM usage_samples WHERE observed_at_utc < $cutoff;
            DELETE FROM daily_usage WHERE day_utc < $cutoffDay;
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$cutoffDay", DateOnly.FromDateTime(cutoff.UtcDateTime).DayNumber);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static DepletionProjection ProjectDepletion(IReadOnlyList<HistoryPoint> points, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(points);
        var recent = points
            .Where(point => point.ObservedAtUtc >= nowUtc.AddHours(-12))
            .OrderBy(static point => point.ObservedAtUtc)
            .ToArray();
        if (recent.Length < 3)
        {
            return new DepletionProjection(false, null, 0m, "projection.insufficient_data");
        }

        var first = recent[0];
        var last = recent[^1];
        var hours = (decimal)(last.ObservedAtUtc - first.ObservedAtUtc).TotalHours;
        if (hours <= 0.1m)
        {
            return new DepletionProjection(false, null, 0m, "projection.insufficient_span");
        }

        var rate = (first.RemainingPercent - last.RemainingPercent) / hours;
        if (rate <= 0.01m)
        {
            return new DepletionProjection(false, null, rate, "projection.not_depleting");
        }

        var hoursRemaining = last.RemainingPercent / rate;
        if (hoursRemaining is < 0 or > 24 * 30)
        {
            return new DepletionProjection(false, null, rate, "projection.out_of_range");
        }

        return new DepletionProjection(
            true,
            last.ObservedAtUtc.AddHours((double)hoursRemaining),
            rate,
            "projection.available");
    }

    private static async Task InsertSampleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        UsageSnapshot snapshot,
        UsageLimit limit,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO usage_samples
            (profile_id, account_key, limit_id, observed_at_utc, kind, label, remaining_percent, used_percent, resets_at_utc)
            VALUES ($profile, $account, $limit, $observed, $kind, $label, $remaining, $used, $reset);
            """;
        BindScope(command, snapshot, limit);
        command.Parameters.AddWithValue("$observed", snapshot.ObservedAtUtc.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$kind", (int)limit.Kind);
        command.Parameters.AddWithValue("$label", limit.Label);
        command.Parameters.AddWithValue("$remaining", (double)limit.RemainingPercent);
        command.Parameters.AddWithValue("$used", (double)limit.UsedPercent);
        command.Parameters.AddWithValue("$reset", limit.ResetsAtUtc?.ToUnixTimeSeconds() is { } reset ? reset : DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertDailyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        UsageSnapshot snapshot,
        UsageLimit limit,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO daily_usage
            (profile_id, account_key, limit_id, day_utc, min_remaining, max_remaining, first_remaining, last_remaining, sample_count)
            VALUES ($profile, $account, $limit, $day, $remaining, $remaining, $remaining, $remaining, 1)
            ON CONFLICT(profile_id, account_key, limit_id, day_utc) DO UPDATE SET
                min_remaining=MIN(min_remaining, excluded.min_remaining),
                max_remaining=MAX(max_remaining, excluded.max_remaining),
                last_remaining=excluded.last_remaining,
                sample_count=sample_count + 1;
            """;
        BindScope(command, snapshot, limit);
        command.Parameters.AddWithValue("$day", DateOnly.FromDateTime(snapshot.ObservedAtUtc.UtcDateTime).DayNumber);
        command.Parameters.AddWithValue("$remaining", (double)limit.RemainingPercent);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void BindScope(SqliteCommand command, UsageSnapshot snapshot, UsageLimit limit)
    {
        command.Parameters.AddWithValue("$profile", snapshot.ProfileId.ToString("D"));
        command.Parameters.AddWithValue("$account", snapshot.Account.StorageKey);
        command.Parameters.AddWithValue("$limit", limit.Identity);
    }

    private static void ValidateScope(Guid profileId, string accountKey, string limitIdentity)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("Profile ID cannot be empty.", nameof(profileId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(limitIdentity);
    }

    private static decimal ToDecimal(double value) =>
        Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);
}
