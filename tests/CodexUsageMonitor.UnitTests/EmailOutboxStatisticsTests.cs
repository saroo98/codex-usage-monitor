using CodexUsageMonitor.Persistence.Database;
using CodexUsageMonitor.Persistence.Outbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexUsageMonitor.UnitTests;

[TestClass]
public sealed class EmailOutboxStatisticsTests
{
    [TestMethod]
    public async Task StatisticsSeparatePendingAndTerminalRowsWithoutReadingPayloads()
    {
        var root = Path.Combine(Path.GetTempPath(), "cum-outbox-stats", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var database = new UsageDatabase(
                new SqliteConnectionFactory(Path.Combine(root, "data.db")),
                NullLogger<UsageDatabase>.Instance);
            var repository = new EmailOutboxRepository(database);
            var now = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
            var pending = new EmailOutboxItem(
                Guid.NewGuid(), "pending", Guid.NewGuid(), "account", "{}", now, now.AddMinutes(5),
                now.AddHours(1), 0, null, null, null, null);
            var terminal = new EmailOutboxItem(
                Guid.NewGuid(), "terminal", Guid.NewGuid(), "account", "{}", now, now,
                now.AddHours(1), 1, "email.auth_failed", null, null, null);
            Assert.IsTrue(await repository.TryEnqueueAsync(pending, CancellationToken.None));
            Assert.IsTrue(await repository.TryEnqueueAsync(terminal, CancellationToken.None));
            await repository.MarkTerminalAsync(terminal.Id, now.AddSeconds(1), "email.permanent_failure", CancellationToken.None);

            var statistics = await repository.GetStatisticsAsync(now, CancellationToken.None);

            Assert.AreEqual(1, statistics.PendingCount);
            Assert.AreEqual(1, statistics.TerminalCount);
            Assert.AreEqual(now.AddMinutes(5), statistics.NextAvailableAtUtc);
            Assert.AreEqual("email.auth_failed", statistics.LastSafeErrorCode);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
