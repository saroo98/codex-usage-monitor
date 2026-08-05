using CodexUsageMonitor.Updater.Install;

namespace CodexUsageMonitor.UnitTests;

[TestClass]
public sealed class UpdatePathSecurityTests
{
    [TestMethod]
    public void DescendantValidationRejectsSiblingPrefixAndParentEscape()
    {
        var root = Path.Combine(Path.GetTempPath(), "cum-path-security", Guid.NewGuid().ToString("N"));
        var child = Path.Combine(root, "child", "file.bin");
        var siblingPrefix = root + "-other";

        UpdatePathSecurity.EnsureDescendant(child, root, "escape");
        Assert.ThrowsExactly<InvalidDataException>(() =>
            UpdatePathSecurity.EnsureDescendant(siblingPrefix, root, "escape"));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            UpdatePathSecurity.EnsureDescendant(root, root, "escape"));
    }

    [TestMethod]
    public void RegularFileValidationRejectsDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cum-path-security", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.ThrowsExactly<InvalidDataException>(() =>
                UpdatePathSecurity.EnsureRegularFile(directory, "unsafe"));
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [TestMethod]
    public void ReparsePointInPathIsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "cum-path-security", Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "target");
        var link = Path.Combine(root, "link");
        Directory.CreateDirectory(target);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, target);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                Assert.Inconclusive($"Symbolic-link creation is unavailable: {exception.GetType().Name}");
                return;
            }

            Assert.ThrowsExactly<InvalidDataException>(() =>
                UpdatePathSecurity.EnsureNoReparsePoints(Path.Combine(link, "payload")));
            Assert.ThrowsExactly<InvalidDataException>(() =>
                UpdatePathSecurity.DeleteDirectoryTree(link, "unsafe"));
            Assert.IsTrue(Directory.Exists(target));
        }
        finally
        {
            try
            {
                if (UpdatePathSecurity.PathEntryExists(link))
                {
                    Directory.Delete(link);
                }

                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
            }
        }
    }
}
