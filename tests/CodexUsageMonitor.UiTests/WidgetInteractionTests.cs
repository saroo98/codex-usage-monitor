using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CodexUsageMonitor.App.Views;
using CodexUsageMonitor.Core.Settings;
using CodexUsageMonitor.Windows.Windowing;
using System.Runtime.InteropServices;

namespace CodexUsageMonitor.UiTests;

[TestClass]
public sealed class WidgetInteractionTests
{
    [TestMethod]
    public void DraggingUnlockedWidgetChangesWindowPosition()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Window? window = null;
            try
            {
                window = new Window
                {
                    Width = 208,
                    Height = 60,
                    Left = 420,
                    Top = 320,
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    Topmost = true,
                };
                using var controller = new WidgetDragController(
                    window,
                    static () => false,
                    static () => false,
                    new MonitorPlacementService());
                window.Show();
                window.Activate();
                window.UpdateLayout();

                var beforeLeft = window.Left;
                var beforeTop = window.Top;
                var origin = window.PointToScreen(new Point(window.ActualWidth / 2, window.ActualHeight / 2));
                var frame = new DispatcherFrame();
                _ = Task.Run(async () =>
                {
                    NativeMouse.SetCursorPos((int)origin.X, (int)origin.Y);
                    await Task.Delay(100);
                    NativeMouse.MouseEvent(NativeMouse.LeftDown, 0, 0, 0, UIntPtr.Zero);
                    for (var step = 1; step <= 10; step++)
                    {
                        NativeMouse.SetCursorPos((int)origin.X + (step * 16), (int)origin.Y + (step * 8));
                        await Task.Delay(35);
                    }

                    NativeMouse.MouseEvent(NativeMouse.LeftUp, 0, 0, 0, UIntPtr.Zero);
                    await Task.Delay(150);
                    _ = window.Dispatcher.BeginInvoke(() => frame.Continue = false);
                });

                var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                timeout.Tick += (_, _) =>
                {
                    timeout.Stop();
                    frame.Continue = false;
                };
                timeout.Start();
                Dispatcher.PushFrame(frame);

                Assert.IsTrue(
                    Math.Abs(window.Left - beforeLeft) >= 80 || Math.Abs(window.Top - beforeTop) >= 40,
                    $"The unlocked widget did not move. Before=({beforeLeft},{beforeTop}), after=({window.Left},{window.Top}).");
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                window?.Close();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            Assert.Fail($"Dragging the unlocked widget failed: {failure}");
        }
    }

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

    private static class NativeMouse
    {
        internal const uint LeftDown = 0x0002;
        internal const uint LeftUp = 0x0004;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll", EntryPoint = "mouse_event")]
        internal static extern void MouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    }
}
