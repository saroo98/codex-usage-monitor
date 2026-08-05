using System.Windows;
using System.Windows.Controls;
using CodexUsageMonitor.Migration.Execution;

namespace CodexUsageMonitor.App.Views;

public partial class OnboardingWindow : Window
{
    public OnboardingWindow(LegacyMigrationRuntimeState migrationState)
    {
        ArgumentNullException.ThrowIfNull(migrationState);
        InitializeComponent();
        Loaded += (_, _) => ApplyAdaptiveLayout();
        SizeChanged += (_, _) => ApplyAdaptiveLayout();
        ApplyMigrationStatus(migrationState.Migration);
    }

    public bool OpenSettingsAfterClose { get; private set; }

    private void ApplyAdaptiveLayout()
    {
        if (!IsInitialized) return;
        var compact = ActualWidth < 620;
        ContentGap.Width = compact ? new GridLength(0) : new GridLength(18);
        RightContentColumn.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(RightCards, compact ? 0 : 2);
        Grid.SetRow(RightCards, compact ? 1 : 0);
    }

    private void ApplyMigrationStatus(LegacyMigrationResult? migration)
    {
        if (migration?.MigrationFound != true)
        {
            return;
        }

        MigrationCard.Visibility = Visibility.Visible;
        var version = string.IsNullOrWhiteSpace(migration.LegacyVersion)
            ? "an earlier version"
            : $"version {migration.LegacyVersion}";
        if (migration.SafeErrorCode is not null)
        {
            MigrationTitle.Text = "Previous notifier left unchanged";
            MigrationBody.Text = migration.SafeErrorCode switch
            {
                "migration.config_missing" => "A previous installation was detected without a readable configuration. No settings or tasks were changed.",
                "migration.marker_invalid" => "The previous migration record could not be trusted. Its Scheduled Tasks remain unchanged.",
                _ => "The previous installation could not be imported safely. Its files and Scheduled Tasks remain unchanged.",
            };
            return;
        }

        MigrationTitle.Text = "Previous notifier imported safely";
        var backup = string.IsNullOrWhiteSpace(migration.BackupArchive)
            ? "A backup location is not available."
            : $"Backup: {Path.GetFileName(migration.BackupArchive)}.";
        MigrationBody.Text = $"Settings from {version} were imported. {backup} The old Scheduled Tasks were not changed and can be retired later from Diagnostics after a fresh Codex reading is confirmed.";
    }

    private void OnSettingsClick(object sender, RoutedEventArgs eventArgs)
    {
        OpenSettingsAfterClose = true;
        DialogResult = true;
    }

    private void OnStartClick(object sender, RoutedEventArgs eventArgs)
    {
        DialogResult = true;
    }
}
