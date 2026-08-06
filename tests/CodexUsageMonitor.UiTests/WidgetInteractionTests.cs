using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Reflection;
using CodexUsageMonitor.App.ViewModels;
using CodexUsageMonitor.App.Views;
using CodexUsageMonitor.Core.Settings;
using CodexUsageMonitor.Windows.Windowing;
using System.Runtime.InteropServices;

namespace CodexUsageMonitor.UiTests;

[TestClass]
public sealed class WidgetInteractionTests
{
    [TestMethod]
    public void WidgetSettingsPreserveTaskbarOverlapPreference()
    {
        var defaults = new WidgetSettings();
        Assert.IsFalse(defaults.AllowTaskbarOverlap, "Taskbar overlap must remain opt-in for existing installations.");

        var section = new WidgetSettingsSectionViewModel();
        section.Load(defaults with { AllowTaskbarOverlap = true });

        Assert.IsTrue(section.AllowTaskbarOverlap);
        Assert.IsTrue(section.ApplyTo(defaults).AllowTaskbarOverlap);
    }

    [TestMethod]
    public void PlacementAreaUsesFullMonitorOnlyWhenTaskbarOverlapIsAllowed()
    {
        var bounds = new DipRect(0, 0, 1920, 1080);
        var workArea = new DipRect(0, 0, 1920, 1040);
        var monitor = new MonitorWorkArea("DISPLAY", bounds, workArea, 1, 1, true);

        Assert.AreEqual(workArea, MonitorPlacementService.SelectPlacementArea(monitor, allowTaskbarOverlap: false));
        Assert.AreEqual(bounds, MonitorPlacementService.SelectPlacementArea(monitor, allowTaskbarOverlap: true));
    }

    [TestMethod]
    [DataRow(WidgetSize.Medium, 208d, 60d)]
    [DataRow(WidgetSize.Small, 148d, 42d)]
    [DataRow(WidgetSize.ExtraSmall, 104d, 30d)]
    public void WidgetSizeIgnoresCorruptedPersistedDimensions(WidgetSize size, double expectedWidth, double expectedHeight)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = WidgetWindow.CreateVisualEvidenceWindow(new CorruptedWidgetSizeModel(), size);

                Assert.AreEqual(expectedWidth, window.Width, "The widget width must come from its selected presentation mode.");
                Assert.AreEqual(expectedHeight, window.Height, "The widget height must come from its selected presentation mode.");
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
            Assert.Fail($"Canonical widget sizing verification failed: {failure}");
        }
    }

    [TestMethod]
    public void SettingsComboBoxTextIsVerticallyCentered()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var repositoryRoot = FindRepositoryRoot();
                var resources = LoadDictionary(Path.Combine(repositoryRoot, "src", "CodexUsageMonitor.App", "Themes", "Controls.xaml"));
                var comboBox = new ComboBox
                {
                    Style = (Style)resources[typeof(ComboBox)],
                };

                Assert.AreEqual(
                    VerticalAlignment.Center,
                    comboBox.VerticalContentAlignment,
                    "Selected values must be vertically centered in every settings dropdown.");

                var item = new ComboBoxItem
                {
                    Style = (Style)resources[typeof(ComboBoxItem)],
                };
                Assert.AreEqual(
                    VerticalAlignment.Center,
                    item.VerticalContentAlignment,
                    "Dropdown choices must use the same vertical alignment as the selected value.");
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
            Assert.Fail($"Settings dropdown alignment verification failed: {failure}");
        }
    }

    [TestMethod]
    public void PrimaryButtonTextRemainsReadableWhenHovered()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Window? window = null;
            try
            {
                var repositoryRoot = FindRepositoryRoot();
                var resources = new ResourceDictionary();
                resources.MergedDictionaries.Add(LoadDictionary(Path.Combine(repositoryRoot, "src", "CodexUsageMonitor.App", "Themes", "Light.xaml")));
                resources.MergedDictionaries.Add(LoadDictionary(Path.Combine(repositoryRoot, "src", "CodexUsageMonitor.App", "Themes", "Controls.xaml")));

                var button = new Button
                {
                    Content = "_Save",
                    Style = (Style)resources["PrimaryButtonStyle"],
                    Width = 100,
                    Height = 40,
                };
                window = new Window
                {
                    Resources = resources,
                    Content = button,
                    Width = 260,
                    Height = 150,
                    Left = 520,
                    Top = 320,
                    Topmost = true,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                };
                window.Show();
                window.Activate();
                window.UpdateLayout();
                button.ApplyTemplate();

                SetIsMouseOver(button, true);
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

                var chrome = (Border?)button.Template.FindName("Chrome", button)
                    ?? throw new AssertFailedException("The button template chrome was not created.");
                var foreground = ((SolidColorBrush)button.Foreground).Color;
                var background = ((SolidColorBrush)chrome.Background).Color;
                var contrast = ContrastRatio(foreground, background);

                Assert.IsGreaterThanOrEqualTo(4.5, contrast, $"Hovered Save contrast was only {contrast:F2}:1.");
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
            Assert.Fail($"Hovered primary button verification failed: {failure}");
        }
    }

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

    private sealed class CorruptedWidgetSizeModel
    {
        public double Width => 148;
        public double Height => 1023;
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

    private static void SetIsMouseOver(UIElement element, bool value)
    {
        var field = typeof(UIElement).GetField("IsMouseOverPropertyKey", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new AssertFailedException("WPF's IsMouseOver dependency-property key was not found.");
        var key = (DependencyPropertyKey?)field.GetValue(null)
            ?? throw new AssertFailedException("WPF's IsMouseOver dependency-property key was unavailable.");
        element.SetValue(key, value);
    }

    private static ResourceDictionary LoadDictionary(string path) =>
        (ResourceDictionary)XamlReader.Parse(File.ReadAllText(path));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new AssertFailedException("The repository root could not be located.");
    }

    private static double ContrastRatio(Color first, Color second)
    {
        static double Luminance(Color color)
        {
            static double Linearize(byte channel)
            {
                var value = channel / 255d;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }

            return (0.2126 * Linearize(color.R)) + (0.7152 * Linearize(color.G)) + (0.0722 * Linearize(color.B));
        }

        var lighter = Math.Max(Luminance(first), Luminance(second));
        var darker = Math.Min(Luminance(first), Luminance(second));
        return (lighter + 0.05) / (darker + 0.05);
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
