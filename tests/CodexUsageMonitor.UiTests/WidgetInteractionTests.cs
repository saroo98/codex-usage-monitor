using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CodexUsageMonitor.App.Views;
using CodexUsageMonitor.Core.Settings;

namespace CodexUsageMonitor.UiTests;

[TestClass]
public sealed class WidgetInteractionTests
{
    [TestMethod]
    public void OpeningContextMenuDoesNotWriteReadOnlyWidgetState()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var application = System.Windows.Application.Current ?? new System.Windows.Application();
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("/CodexUsageMonitor;component/Themes/Controls.xaml", UriKind.Relative),
                });

                var window = WidgetWindow.CreateVisualEvidenceWindow(new ContextMenuModel(), WidgetSize.Medium);
                window.Left = -10_000;
                window.Top = -10_000;
                window.Show();

                var surface = (Border)window.VisualEvidenceSurface;
                var menu = surface.ContextMenu ?? throw new AssertFailedException("The widget context menu is missing.");
                menu.PlacementTarget = surface;
                menu.IsOpen = true;
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

                Assert.IsTrue(menu.IsOpen);
                menu.IsOpen = false;
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            Assert.Fail($"Opening the widget context menu failed: {failure}");
        }
    }

    private sealed class ContextMenuModel
    {
        public bool IsLocked => false;
        public bool IsClickThrough => false;
        public WidgetSize Size => WidgetSize.Medium;
        public ICommand RefreshCommand { get; } = new NoOpCommand();
        public ICommand SetSizeCommand { get; } = new NoOpCommand();
        public ICommand ToggleLockCommand { get; } = new NoOpCommand();
        public ICommand ToggleClickThroughCommand { get; } = new NoOpCommand();
        public ICommand RedeemResetCreditCommand { get; } = new NoOpCommand();
        public ICommand OpenSettingsCommand { get; } = new NoOpCommand();
        public ICommand ExitCommand { get; } = new NoOpCommand();
    }

    private sealed class NoOpCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) { }
    }
}
