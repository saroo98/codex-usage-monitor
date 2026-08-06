using System.Windows;
using CodexUsageMonitor.App.Runtime;

namespace CodexUsageMonitor.UiTests;

[TestClass]
public sealed class WidgetWindowSessionTests
{
    [TestMethod]
    public void ShowAfterWindowClosedCreatesAndShowsANewWindow()
    {
        var created = new List<FakeWidgetWindow>();
        using var session = new WidgetWindowSession(() =>
        {
            var window = new FakeWidgetWindow();
            created.Add(window);
            return window;
        });

        session.Show();
        created[0].CloseExternally();
        session.Show();

        Assert.AreEqual(2, created.Count);
        Assert.AreEqual(1, created[0].ShowCount);
        Assert.AreEqual(1, created[1].ShowCount);
        Assert.IsTrue(session.IsVisible);
    }

    private sealed class FakeWidgetWindow : IWidgetWindow
    {
        public event EventHandler? Closed;

        public bool IsVisible { get; private set; }
        public Window OwnerWindow => throw new NotSupportedException();
        public int ShowCount { get; private set; }

        public void ShowWithoutActivation()
        {
            ShowCount++;
            IsVisible = true;
        }

        public void Hide() => IsVisible = false;
        public void RestorePlacement() { }
        public void CloseForExit() => CloseExternally();

        public void CloseExternally()
        {
            IsVisible = false;
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }
}
