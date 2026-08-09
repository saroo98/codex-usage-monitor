using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using CodexUsageMonitor.App.Services;
using CodexUsageMonitor.App.ViewModels;
using CodexUsageMonitor.Email.OAuth;
using Microsoft.Win32;

namespace CodexUsageMonitor.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel = null!;
    private readonly EmailCredentialService _emailCredentials = null!;
    private readonly OAuthConnectionService _oauthConnections = null!;
    private CancellationTokenSource? _oauthOperation;

    private SettingsWindow()
    {
        InitializeComponent();
    }

    public SettingsWindow(
        SettingsViewModel viewModel,
        EmailCredentialService emailCredentials,
        OAuthConnectionService oauthConnections) : this()
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _emailCredentials = emailCredentials ?? throw new ArgumentNullException(nameof(emailCredentials));
        _oauthConnections = oauthConnections ?? throw new ArgumentNullException(nameof(oauthConnections));
        DataContext = _viewModel;
        _viewModel.RequestClose += OnRequestClose;
        Loaded += OnLoaded;
        SizeChanged += OnWindowSizeChanged;
        Closed += OnClosed;
    }

    internal static SettingsWindow CreateVisualEvidenceWindow(object dataContext)
    {
        ArgumentNullException.ThrowIfNull(dataContext);
        return new SettingsWindow { DataContext = dataContext };
    }

    internal FrameworkElement VisualEvidenceSurface => (FrameworkElement)Content;

    public void ActivateSection(SettingsSection section)
    {
        _viewModel.SelectedSection = section;
        if (!IsVisible) Show();
        if (WindowState is WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private async void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        ApplyAdaptiveLayout();
        _viewModel.Email.GoogleConnectionAvailable = _oauthConnections.Registrations.GoogleAvailable;
        _viewModel.Email.MicrosoftConnectionAvailable = _oauthConnections.Registrations.MicrosoftAvailable;
        await RefreshCredentialStatusAsync().ConfigureAwait(true);
        await RefreshOAuthStatusAsync().ConfigureAwait(true);
        await _viewModel.RefreshMigrationStatusAsync(CancellationToken.None).ConfigureAwait(true);
        await _viewModel.RefreshDiagnosticsAsync(CancellationToken.None).ConfigureAwait(true);
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs eventArgs) => ApplyAdaptiveLayout();

    private void ApplyAdaptiveLayout()
    {
        if (!IsInitialized) return;
        var compact = ActualWidth < 760;
        NavigationColumn.Width = compact ? new GridLength(0) : new GridLength(220);
        NavigationRail.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactNavigation.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        ContentScrollViewer.Margin = compact ? new Thickness(0, 58, 0, 0) : default;
    }

    private async void OnStoreSmtpPassword(object sender, RoutedEventArgs eventArgs)
    {
        if (string.IsNullOrWhiteSpace(_viewModel.Email.SenderAddress))
        {
            _viewModel.ReportEmailOperationFailure("Enter a valid sender address before storing a password.");
            return;
        }

        using SecureString password = SmtpPasswordBox.SecurePassword.Copy();
        SmtpPasswordBox.Clear();
        try
        {
            var status = await _emailCredentials.StoreSmtpPasswordAsync(
                _viewModel.Email.Provider,
                _viewModel.Email.SenderAddress,
                password,
                CancellationToken.None).ConfigureAwait(true);
            _viewModel.SetEmailCredentialStatus(status);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _viewModel.ReportEmailOperationFailure($"The SMTP password was not stored: {exception.Message}");
        }
    }

    private async void OnRemoveSmtpPassword(object sender, RoutedEventArgs eventArgs)
    {
        SmtpPasswordBox.Clear();
        try
        {
            var status = await _emailCredentials.RemoveSmtpPasswordAsync(CancellationToken.None).ConfigureAwait(true);
            _viewModel.SetEmailCredentialStatus(status);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _viewModel.ReportEmailOperationFailure($"The SMTP password was not removed: {exception.Message}");
        }
    }

    private async Task RefreshCredentialStatusAsync()
    {
        try
        {
            var status = await _emailCredentials.GetSmtpPasswordStatusAsync(
                _viewModel.Email.SenderAddress,
                CancellationToken.None).ConfigureAwait(true);
            _viewModel.SetEmailCredentialStatus(status);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _viewModel.ReportEmailOperationFailure($"Credential status is unavailable: {exception.Message}");
        }
    }

    private async void OnConnectOAuth(object sender, RoutedEventArgs eventArgs)
    {
        if (!_viewModel.Email.IsOAuthProvider)
        {
            _viewModel.ReportEmailOperationFailure("Select Gmail or Outlook / Microsoft 365 first.");
            return;
        }

        _oauthOperation?.Cancel();
        _oauthOperation?.Dispose();
        _oauthOperation = new CancellationTokenSource();
        _viewModel.Email.OAuthBusy = true;
        try
        {
            OAuthConnectionStatus status;
            if (_viewModel.Email.Provider is CodexUsageMonitor.Core.Settings.EmailProviderMode.Microsoft365)
            {
                status = await _oauthConnections.ConnectMicrosoftAsync(_oauthOperation.Token).ConfigureAwait(true);
            }
            else
            {
                status = await _oauthConnections.ConnectGoogleAsync(_oauthOperation.Token).ConfigureAwait(true);
            }

            _viewModel.SetOAuthConnectionStatus(status);
        }
        catch (OperationCanceledException)
        {
            _viewModel.ReportEmailOperationFailure("OAuth connection was cancelled. No new authorization is active.");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Net.Http.HttpRequestException or OAuthProtocolException)
        {
            _viewModel.ReportEmailOperationFailure($"OAuth connection failed: {SafeOAuthMessage(exception)}");
        }
        finally
        {
            _viewModel.Email.OAuthBusy = false;
        }
    }

    private async void OnDisconnectOAuth(object sender, RoutedEventArgs eventArgs)
    {
        _oauthOperation?.Cancel();
        _viewModel.Email.OAuthBusy = true;
        try
        {
            var status = await _oauthConnections.DisconnectAsync(CancellationToken.None).ConfigureAwait(true);
            _viewModel.SetOAuthConnectionStatus(status);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _viewModel.ReportEmailOperationFailure("OAuth connection was not removed safely. Check Diagnostics for the recorded error code.");
        }
        finally
        {
            _viewModel.Email.OAuthBusy = false;
        }
    }

    private async Task RefreshOAuthStatusAsync()
    {
        try
        {
            var status = await _oauthConnections.GetStatusAsync(CancellationToken.None).ConfigureAwait(true);
            _viewModel.SetOAuthConnectionStatus(status);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _viewModel.ReportEmailOperationFailure("OAuth connection status is unavailable. Check Diagnostics for details.");
        }
    }

    private static string SafeOAuthMessage(Exception exception) => exception is OAuthProtocolException protocol
        ? protocol.SafeErrorCode switch
        {
            "oauth.access_denied" => "authorization was denied",
            "oauth.expired_token" => "the one-time authorization code expired",
            "oauth.authorization_timeout" => "the browser authorization timed out",
            "oauth.state_mismatch" => "the browser response could not be trusted",
            "oauth.invalid_client" => "the provider rejected the client registration",
            _ => "the provider rejected the authorization request",
        }
        : "the authorization could not be completed safely";

    private void OnCopyDiagnostics(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            Clipboard.SetText(_viewModel.Diagnostics.Summary, TextDataFormat.UnicodeText);
            _viewModel.ReportOperationStatus("Diagnostics copied to the clipboard.");
        }
        catch (ExternalException)
        {
            _viewModel.ReportOperationStatus("The clipboard is currently unavailable. No diagnostic data was changed.");
        }
    }

    private async void OnExportSupportBundle(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            CheckPathExists = true,
            DefaultExt = ".zip",
            FileName = $"CodexUsageMonitor-Support-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip",
            Filter = "ZIP archive (*.zip)|*.zip",
            OverwritePrompt = true,
            Title = "Export redacted support bundle",
        };
        if (dialog.ShowDialog(this) is not true)
        {
            return;
        }

        try
        {
            await _viewModel.ExportSupportBundleToAsync(dialog.FileName, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            _viewModel.ReportOperationStatus(
                $"The support bundle was not created: {CodexUsageMonitor.Core.Diagnostics.SafeDiagnosticRedactor.Redact(exception.Message)}");
        }
    }

    private async void OnRetireLegacyTasks(object sender, RoutedEventArgs eventArgs)
    {
        var confirmation = MessageBox.Show(
            "Disable the old Codex Usage Notifier Scheduled Tasks?\n\n" +
            "The verified migration backup will be kept. The tasks are disabled, not deleted, and can be restored from this page.",
            "Disable old notifier tasks",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation is not MessageBoxResult.Yes)
        {
            return;
        }

        await _viewModel.RetireLegacyTasksAsync(explicitlyConfirmed: true, CancellationToken.None).ConfigureAwait(true);
    }

    private async void OnRestoreLegacyTasks(object sender, RoutedEventArgs eventArgs)
    {
        var confirmation = MessageBox.Show(
            "Restore the old Codex Usage Notifier Scheduled Tasks to their captured enabled state?\n\n" +
            "This can cause both the old and new monitors to run until you close one of them.",
            "Restore old notifier tasks",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirmation is not MessageBoxResult.Yes)
        {
            return;
        }

        await _viewModel.RestoreLegacyTasksAsync(explicitlyConfirmed: true, CancellationToken.None).ConfigureAwait(true);
    }

    private void OnRequestClose(object? sender, bool saved)
    {
        Tag = saved;
        Close();
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        SmtpPasswordBox.Clear();
        _oauthOperation?.Cancel();
        _oauthOperation?.Dispose();
        _oauthOperation = null;
        _viewModel.RequestClose -= OnRequestClose;
        Loaded -= OnLoaded;
        Closed -= OnClosed;
    }
}
