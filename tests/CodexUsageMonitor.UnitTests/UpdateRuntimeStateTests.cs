using CodexUsageMonitor.Application.Updates;

namespace CodexUsageMonitor.UnitTests;

[TestClass]
public sealed class UpdateRuntimeStateTests
{
    [TestMethod]
    public void StateStartsReadyAndPublishesWholeSnapshots()
    {
        var state = new UpdateRuntimeState("1.0.0");
        UpdateRuntimeSnapshot? observed = null;
        state.Changed += (_, value) => observed = value;
        var next = state.Current with
        {
            Status = UpdateRuntimeStatus.Available,
            AvailableVersion = "1.1.0",
            CanPrepare = true,
        };

        state.Set(next);

        Assert.AreSame(next, state.Current);
        Assert.AreSame(next, observed);
        Assert.IsTrue(state.Current.CanPrepare);
        Assert.IsFalse(state.Current.CanInstall);
    }

    [TestMethod]
    public void InstallCapabilityIsExplicitApplicationState()
    {
        var state = new UpdateRuntimeState("1.0.0");

        state.Set(state.Current with
        {
            Status = UpdateRuntimeStatus.Staged,
            CanPrepare = false,
            CanInstall = true,
        });

        Assert.IsTrue(state.Current.CanInstall);
        Assert.IsFalse(state.Current.CanPrepare);
    }
}
