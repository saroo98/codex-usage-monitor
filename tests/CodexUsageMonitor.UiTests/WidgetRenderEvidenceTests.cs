using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexUsageMonitor.App.ResetCredits;
using CodexUsageMonitor.App.Services;
using CodexUsageMonitor.App.ViewModels;
using CodexUsageMonitor.App.Views;
using CodexUsageMonitor.Core.Settings;
using CodexUsageMonitor.Migration.Execution;

namespace CodexUsageMonitor.UiTests;

[TestClass]
public sealed class WidgetRenderEvidenceTests
{
    [TestMethod]
    public void RenderMatrixProducesRedactedEvidenceAtEveryThemeSizeScaleAndState()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                RenderEvidence();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) Assert.Fail($"WPF render evidence generation failed: {failure}");
    }

    private static void RenderEvidence()
    {
        var repository = FindRepositoryRoot();
        var manifestPath = Path.Combine(repository, "tests", "CodexUsageMonitor.UiTests", "Baselines", "widget-render-matrix.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var output = Path.Combine(repository, "artifacts", "ui-evidence", "widget");
        Directory.CreateDirectory(output);

        var application = System.Windows.Application.Current ?? new System.Windows.Application();
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/CodexUsageMonitor;component/Themes/Controls.xaml", UriKind.Relative),
        });

        RenderWidgetMatrix(manifest, output, application);
        RenderPrimaryScreens(repository, application);
    }

    private static void RenderWidgetMatrix(
        JsonDocument manifest,
        string output,
        System.Windows.Application application)
    {
        var expectedCount = manifest.RootElement.GetProperty("expectedScreenshotCount").GetInt32();

        var generated = 0;
        var renderLatency = new List<double>(expectedCount);
        foreach (var theme in new[] { "Light", "Dark", "HighContrast" })
        {
            ApplyTheme(application, theme);
            foreach (var (size, width, height) in Sizes())
            {
                foreach (var scale in new[] { 1d, 1.25d, 1.5d, 2d })
                {
                    foreach (var scenario in Scenarios())
                    {
                        var renderWatch = System.Diagnostics.Stopwatch.StartNew();
                        var model = new WidgetEvidenceModel(width, height, size, scenario);
                        var window = WidgetWindow.CreateVisualEvidenceWindow(model, size);
                        var surface = window.VisualEvidenceSurface;
                        surface.Measure(new Size(width, height));
                        surface.Arrange(new Rect(0, 0, width, height));
                        surface.UpdateLayout();

                        var pixelWidth = (int)Math.Ceiling(width * scale);
                        var pixelHeight = (int)Math.Ceiling(height * scale);
                        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                        bitmap.Render(surface);
                        var fileName = $"{theme.ToLowerInvariant()}-{size.ToString().ToLowerInvariant()}-{scale:0.##}x-{scenario.Id}.png";
                        Save(bitmap, Path.Combine(output, fileName));
                        renderWatch.Stop();
                        renderLatency.Add(renderWatch.Elapsed.TotalMilliseconds);
                        Assert.AreEqual(pixelWidth, bitmap.PixelWidth);
                        Assert.AreEqual(pixelHeight, bitmap.PixelHeight);
                        generated++;
                    }
                }
            }
        }

        Assert.AreEqual(expectedCount, generated);
        var orderedLatency = renderLatency.Order().ToArray();
        var p95 = orderedLatency[(int)Math.Ceiling(orderedLatency.Length * 0.95) - 1];
        var metrics = new
        {
            schemaVersion = 1,
            measurement = "widget-presentation-render",
            sampleCount = orderedLatency.Length,
            p50Milliseconds = Math.Round(orderedLatency[orderedLatency.Length / 2], 3),
            p95Milliseconds = Math.Round(p95, 3),
            maximumMilliseconds = Math.Round(orderedLatency[^1], 3),
            thresholdP95Milliseconds = 100,
            includes = "binding, layout, WPF rendering, PNG encoding, and local artifact write",
        };
        File.WriteAllText(
            Path.Combine(output, "render-metrics.json"),
            JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
        Assert.IsTrue(p95 <= 100, $"Widget presentation render p95 was {p95:0.###} ms.");
    }

    private static void RenderPrimaryScreens(string repository, System.Windows.Application application)
    {
        var manifestPath = Path.Combine(repository, "tests", "CodexUsageMonitor.UiTests", "Baselines", "primary-screen-render-matrix.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var expectedCount = manifest.RootElement.GetProperty("expectedScreenshotCount").GetInt32();
        var output = Path.Combine(repository, "artifacts", "ui-evidence", "screens");
        Directory.CreateDirectory(output);
        var generated = 0;

        foreach (var theme in new[] { "Light", "Dark", "HighContrast" })
        {
            ApplyTheme(application, theme);
            foreach (var scale in new[] { 1d, 1.25d, 1.5d, 2d })
            {
                var settings = SettingsWindow.CreateVisualEvidenceWindow(new SettingsEvidenceModel());
                RenderWindow(settings.VisualEvidenceSurface, 920, 670, scale, Path.Combine(output, FileName(theme, scale, "settings")), application);
                generated++;

                var onboarding = new OnboardingWindow(new LegacyMigrationRuntimeState());
                RenderWindow((FrameworkElement)onboarding.Content, 680, 620, scale, Path.Combine(output, FileName(theme, scale, "onboarding")), application);
                generated++;

                var reset = new ResetCreditConfirmationDialog(new ResetRedemptionIntent(
                    Guid.Empty,
                    Guid.Empty,
                    "synthetic-storage-key",
                    "Account hidden",
                    "synthetic-credit",
                    "Standard reset credit",
                    ["Primary limit", "Secondary limit"],
                    new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero)));
                RenderWindow((FrameworkElement)reset.Content, 540, 520, scale, Path.Combine(output, FileName(theme, scale, "reset-confirmation")), application);
                generated++;

            }
        }

        Assert.AreEqual(expectedCount, generated);
    }

    private static string FileName(string theme, double scale, string screen) =>
        $"{theme.ToLowerInvariant()}-{scale:0.##}x-{screen}.png";

    private static void RenderWindow(
        FrameworkElement surface,
        double width,
        double height,
        double scale,
        string path,
        System.Windows.Application application)
    {
        var background = (Brush)application.FindResource("SurfaceBrush");
        if (surface is System.Windows.Controls.Panel panel) panel.Background = background;
        if (surface is System.Windows.Controls.Control control) control.Background = background;
        surface.Measure(new Size(width, height));
        surface.Arrange(new Rect(0, 0, width, height));
        surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width * scale),
            (int)Math.Ceiling(height * scale),
            96 * scale,
            96 * scale,
            PixelFormats.Pbgra32);
        var backgroundVisual = new DrawingVisual();
        using (var drawing = backgroundVisual.RenderOpen())
        {
            drawing.DrawRectangle(background, null, new Rect(0, 0, width, height));
        }
        bitmap.Render(backgroundVisual);
        bitmap.Render(surface);
        Save(bitmap, path);
    }

    private static void ApplyTheme(System.Windows.Application application, string theme)
    {
        var dictionaries = application.Resources.MergedDictionaries;
        while (dictionaries.Count > 1) dictionaries.RemoveAt(0);
        dictionaries.Insert(0, new ResourceDictionary
        {
            Source = new Uri($"/CodexUsageMonitor;component/Themes/{theme}.xaml", UriKind.Relative),
        });
    }

    private static IEnumerable<(WidgetSize Size, double Width, double Height)> Sizes()
    {
        yield return (WidgetSize.Medium, 208, 60);
        yield return (WidgetSize.Small, 148, 42);
        yield return (WidgetSize.ExtraSmall, 104, 30);
        yield return (WidgetSize.XXS, 48, 48);
    }

    private static IEnumerable<WidgetScenario> Scenarios()
    {
        yield return new("starting", "Starting", WidgetVisualState.Starting, 0, "--%", "Waiting for Codex");
        yield return new("live", "Live", WidgetVisualState.Healthy, 74, "74%", "Resets in 2h 15m");
        yield return new("warning", "Warning", WidgetVisualState.Warning, 18, "18%", "Resets in 47m");
        yield return new("critical", "Critical", WidgetVisualState.Critical, 8, "8%", "Resets in 21m");
        yield return new("depleted", "Depleted", WidgetVisualState.Depleted, 0, "0%", "Waiting for reset");
        yield return new("delayed", "Delayed", WidgetVisualState.Stale, 63, "63%", "Last confirmed 4m ago");
        yield return new("stale", "Stale", WidgetVisualState.Stale, 63, "63%", "Last confirmed 18m ago");
        yield return new("offline", "Offline", WidgetVisualState.Error, 63, "63%", "Last confirmed 18m ago");
        yield return new("authentication-required", "Sign in required", WidgetVisualState.Error, 63, "63%", "Confirmed data retained");
        yield return new("codex-unavailable", "Codex not found", WidgetVisualState.Error, 63, "63%", "Confirmed data retained");
        yield return new("updating", "Updating", WidgetVisualState.Starting, 63, "63%", "Preparing verified update");
        yield return new("recovery", "Recovery", WidgetVisualState.Warning, 63, "63%", "Restoring previous version");
        yield return new("migration", "Migration", WidgetVisualState.Starting, 63, "63%", "Importing local settings");
    }

    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        Assert.IsTrue(stream.Length > 100, $"Rendered evidence was unexpectedly empty: {path}");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CodexUsageMonitor.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed record WidgetScenario(
        string Id,
        string Status,
        WidgetVisualState VisualState,
        decimal Remaining,
        string RemainingText,
        string ResetText);

    private sealed class WidgetEvidenceModel(double width, double height, WidgetSize size, WidgetScenario scenario)
    {
        public double Width { get; set; } = width;
        public double Height { get; set; } = height;
        public WidgetSize Size { get; set; } = size;
        public WidgetVisualState VisualState { get; set; } = scenario.VisualState;
        public decimal RemainingPercent { get; set; } = scenario.Remaining;
        public string RemainingText { get; set; } = scenario.RemainingText;
        public string LimitLabel { get; set; } = "Primary limit";
        public string StatusText { get; set; } = scenario.Status;
        public string ResetText { get; set; } = scenario.ResetText;
        public string AccountText { get; set; } = "Account hidden";
        public bool ShowAccount { get; set; } = true;
        public string ToolTipText => $"{StatusText}. {RemainingText} remaining. {ResetText}.";
    }

    private sealed class SettingsEvidenceModel
    {
        public IReadOnlyList<SettingsSection> Sections { get; } = Enum.GetValues<SettingsSection>();
        public SettingsSection SelectedSection { get; set; } = SettingsSection.General;
        public GeneralEvidenceModel General { get; } = new();
        public UpdateEvidenceModel Updates { get; } = new();
        public string StatusMessage => "Changes are saved only when you select Save.";
    }

    private sealed class GeneralEvidenceModel
    {
        public bool StartWithWindows { get; set; } = true;
        public bool CloseToTray { get; set; } = true;
        public bool LaunchMinimized { get; set; }
        public bool PrivacyMode { get; set; } = true;
        public string Language { get; set; } = "en";
    }

    private sealed class UpdateEvidenceModel
    {
        public string CurrentVersion { get; } = "6.0.0";
        public string AvailableVersion { get; } = "6.0.1";
        public string LastChecked { get; } = "Today, 13:00";
    }
}
