using System.Globalization;
using System.Windows;
using CodexUsageMonitor.App.ResetCredits;

namespace CodexUsageMonitor.App.Views;

public partial class ResetCreditConfirmationDialog : Window
{
    public ResetCreditConfirmationDialog(ResetRedemptionIntent intent)
    {
        Intent = intent ?? throw new ArgumentNullException(nameof(intent));
        InitializeComponent();
        DataContext = new
        {
            intent.AccountLabel,
            intent.ResetCreditLabel,
            AffectedLimitsText = intent.AffectedLimits.Count == 0 ? "Backend-selected usage windows" : string.Join(", ", intent.AffectedLimits),
            ExpiryText = intent.ExpiresAtUtc?.ToLocalTime().ToString("f", CultureInfo.CurrentCulture) ?? "Not supplied by backend",
        };
    }

    public ResetRedemptionIntent Intent { get; }

    public bool Confirmed { get; private set; }

    private void OnConfirmClick(object sender, RoutedEventArgs eventArgs)
    {
        Confirmed = ConfirmationCheckBox.IsChecked is true;
        DialogResult = Confirmed;
    }
}
