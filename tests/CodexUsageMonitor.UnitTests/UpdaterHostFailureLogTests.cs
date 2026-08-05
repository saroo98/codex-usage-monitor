using CodexUsageMonitor.Updater.Install;

namespace CodexUsageMonitor.UnitTests;

[TestClass]
public sealed class UpdaterHostFailureLogTests
{
    [TestMethod]
    public void WritesOnlyBoundedSafeCodesAndRetainsNewestEntries()
    {
        using var fixture = new TemporaryDirectory();
        var start = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < UpdaterHostFailureLog.MaximumLines + 20; index++)
        {
            UpdaterHostFailureLog.TryWrite(
                $"update.test_{index}",
                fixture.Path,
                start.AddSeconds(index));
        }

        var path = UpdaterHostFailureLog.GetLogPath(fixture.Path);
        var lines = File.ReadAllLines(path);
        Assert.AreEqual(UpdaterHostFailureLog.MaximumLines, lines.Length);
        Assert.IsTrue(new FileInfo(path).Length <= UpdaterHostFailureLog.MaximumLogBytes);
        Assert.IsFalse(lines.Any(static line => line.Contains("update.test_0", StringComparison.Ordinal)));
        Assert.IsTrue(lines[^1].EndsWith("update.test_83", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SanitizesUntrustedTextWithoutPersistingIt()
    {
        using var fixture = new TemporaryDirectory();
        const string untrusted = "secret path C:\\Users\\person\\token.txt\nsecond line";

        UpdaterHostFailureLog.TryWrite(
            untrusted,
            fixture.Path,
            new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero));

        var content = File.ReadAllText(UpdaterHostFailureLog.GetLogPath(fixture.Path));
        StringAssert.Contains(content, UpdaterHostFailureLog.UnclassifiedFailureCode);
        Assert.IsFalse(content.Contains("secret", StringComparison.Ordinal));
        Assert.IsFalse(content.Contains("Users", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("update.valid_code-1", true)]
    [DataRow("", false)]
    [DataRow("update invalid", false)]
    [DataRow("update.invalid/path", false)]
    public void SafeCodeValidationIsStrict(string value, bool expected) =>
        Assert.AreEqual(expected, UpdaterHostFailureLog.IsSafeErrorCode(value));

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "cum-updater-log",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
