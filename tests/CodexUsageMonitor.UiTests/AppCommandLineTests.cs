using CodexUsageMonitor.Windows.Runtime;

namespace CodexUsageMonitor.UiTests;

[TestClass]
public sealed class AppCommandLineTests
{
    [TestMethod]
    public void DefaultLaunchShowsWidget()
    {
        var result = AppCommandLine.Parse([]);

        Assert.IsTrue(result.IsValid);
        Assert.IsFalse(result.Background);
        Assert.AreEqual(ActivationCommandNames.ShowWidget, result.Commands.Single().Name);
        Assert.IsTrue(result.ToActivationMessage().TryValidate(out _));
    }

    [TestMethod]
    public void BackgroundLaunchHidesWidget()
    {
        var result = AppCommandLine.Parse(["--background"]);

        Assert.IsTrue(result.IsValid);
        Assert.IsTrue(result.Background);
        Assert.AreEqual(ActivationCommandNames.HideWidget, result.Commands.Single().Name);
    }

    [TestMethod]
    public void SettingsSectionIsNormalized()
    {
        var result = AppCommandLine.Parse(["--settings=diagnostics"]);

        Assert.IsTrue(result.IsValid);
        var command = result.Commands.Single();
        Assert.AreEqual(ActivationCommandNames.OpenSettings, command.Name);
        Assert.AreEqual("Diagnostics", command.Value);
    }

    [TestMethod]
    public void AfterUpdateRequiresMatchingTransactionAndMarker()
    {
        var transaction = Guid.NewGuid();
        var marker = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"{transaction:N}.ready"));

        var result = AppCommandLine.Parse(["--after-update", transaction.ToString("D"), "--health-marker", marker]);

        Assert.IsTrue(result.IsValid);
        var command = result.Commands.Single(item => item.Name == ActivationCommandNames.UpdateHealth);
        Assert.IsTrue(StartupHealthRequest.TryDecode(command.Value, out var decoded));
        Assert.AreEqual(transaction, decoded.TransactionId);
        Assert.AreEqual(marker, decoded.HealthMarkerPath);
    }

    [TestMethod]
    public void IncompleteAfterUpdateIsRejected()
    {
        var result = AppCommandLine.Parse(["--after-update", Guid.NewGuid().ToString("D")]);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("command_line.update_health_incomplete", result.SafeErrorCode);
    }

    [TestMethod]
    public void UnknownOptionIsRejected()
    {
        var result = AppCommandLine.Parse(["--unknown"]);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(2, result.ExitCode);
        Assert.AreEqual("command_line.unknown_option", result.SafeErrorCode);
    }
    [TestMethod]
    public void PortableUpdateCommandsAreMarkedForPackageIdentityRejection()
    {
        var transactionId = Guid.NewGuid();
        var marker = Path.Combine(Path.GetTempPath(), transactionId.ToString("N") + ".ready");

        var afterUpdate = AppCommandLine.Parse([
            "--after-update", transactionId.ToString("D"),
            "--health-marker", marker,
        ]);
        var rolledBack = AppCommandLine.Parse([
            "--update-rolled-back", transactionId.ToString("D"),
        ]);
        var normal = AppCommandLine.Parse(["--show-widget"]);

        Assert.IsTrue(afterUpdate.IsValid);
        Assert.IsTrue(afterUpdate.HasPortableUpdateCommand);
        Assert.IsTrue(rolledBack.IsValid);
        Assert.IsTrue(rolledBack.HasPortableUpdateCommand);
        Assert.IsFalse(normal.HasPortableUpdateCommand);
    }

}
