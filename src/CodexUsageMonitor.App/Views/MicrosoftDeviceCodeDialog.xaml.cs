using System.Diagnostics;
using System.Windows;
using CodexUsageMonitor.App.Services;

namespace CodexUsageMonitor.App.Views;

public partial class MicrosoftDeviceCodeDialog : Window
{
    private readonly MicrosoftOAuthPrompt _challenge;
    private readonly Action _cancel;

    public MicrosoftDeviceCodeDialog(MicrosoftOAuthPrompt challenge, Action cancel)
    {
        _challenge = challenge ?? throw new ArgumentNullException(nameof(challenge));
        _cancel = cancel ?? throw new ArgumentNullException(nameof(cancel));
        InitializeComponent();
        UserCodeText.Text = challenge.UserCode;
        ProviderMessageText.Text = string.IsNullOrWhiteSpace(challenge.ProviderMessage)
            ? "Complete authorization in your browser. No email password is shared with this application."
            : challenge.ProviderMessage;
        ExpiryText.Text = $"This code expires at {challenge.ExpiresAtUtc.ToLocalTime():t}.";
        Closing += OnClosing;
    }

    public void CloseAfterCompletion()
    {
        Closing -= OnClosing;
        Close();
    }

    private void OnCopyCode(object sender, RoutedEventArgs eventArgs) => Clipboard.SetText(_challenge.UserCode);

    private void OnOpenPage(object sender, RoutedEventArgs eventArgs) =>
        Process.Start(new ProcessStartInfo(_challenge.VerificationUri.AbsoluteUri) { UseShellExecute = true });

    private void OnCancel(object sender, RoutedEventArgs eventArgs)
    {
        _cancel();
        Close();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs eventArgs) => _cancel();
}
