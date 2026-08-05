using CodexUsageMonitor.Windows.Runtime;

namespace CodexUsageMonitor.UiTests;

[TestClass]
public sealed class ActivationMessageTests
{
    [TestMethod]
    public void RejectsUnknownCommand()
    {
        var message = new ActivationMessage(
            ActivationMessage.CurrentVersion,
            [new ActivationCommand("run-arbitrary-code")]);

        Assert.IsFalse(message.TryValidate(out var code));
        Assert.AreEqual("activation.invalid_command", code);
    }

    [TestMethod]
    public void RejectsValueForValueLessCommand()
    {
        var message = new ActivationMessage(
            ActivationMessage.CurrentVersion,
            [new ActivationCommand(ActivationCommandNames.Exit, "unexpected")]);

        Assert.IsFalse(message.TryValidate(out var code));
        Assert.AreEqual("activation.invalid_value", code);
    }

    [TestMethod]
    public void StartupHealthPayloadRoundTripsUnicodePath()
    {
        var transaction = Guid.NewGuid();
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Codex usage", "سلام.ready"));
        var encoded = new StartupHealthRequest(transaction, path).Encode();

        Assert.IsTrue(StartupHealthRequest.TryDecode(encoded, out var decoded));
        Assert.AreEqual(transaction, decoded.TransactionId);
        Assert.AreEqual(path, decoded.HealthMarkerPath);
    }
}
