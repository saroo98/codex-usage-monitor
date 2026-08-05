using System.Windows;
using CodexUsageMonitor.Core.Settings;

namespace CodexUsageMonitor.App.Services;

public sealed class ThemeManager
{
    private const string ThemePrefix = "/CodexUsageMonitor;component/Themes/";
    private readonly System.Windows.Application _application;

    public ThemeManager(System.Windows.Application application)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
    }

    public void Apply(AppTheme requestedTheme)
    {
        var effective = SystemParameters.HighContrast
            ? AppTheme.HighContrast
            : requestedTheme is AppTheme.System
                ? (SystemThemeDetector.IsDark() ? AppTheme.Dark : AppTheme.Light)
                : requestedTheme;
        var source = effective switch
        {
            AppTheme.Light => "Light.xaml",
            AppTheme.HighContrast => "HighContrast.xaml",
            _ => "Dark.xaml",
        };

        var dictionaries = _application.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(static dictionary =>
            dictionary.Source?.OriginalString.Contains("/Themes/", StringComparison.OrdinalIgnoreCase) is true &&
            !dictionary.Source.OriginalString.EndsWith("Controls.xaml", StringComparison.OrdinalIgnoreCase));
        var replacement = new ResourceDictionary { Source = new Uri(ThemePrefix + source, UriKind.Relative) };
        if (existing is null)
        {
            dictionaries.Insert(0, replacement);
        }
        else
        {
            dictionaries[dictionaries.IndexOf(existing)] = replacement;
        }
    }
}

internal static class SystemThemeDetector
{
    public static bool IsDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                writable: false);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return true;
        }
    }
}
