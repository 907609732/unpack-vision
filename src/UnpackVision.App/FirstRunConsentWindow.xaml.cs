using System.Windows;

namespace UnpackVision.App;

public partial class FirstRunConsentWindow : Window
{
    public FirstRunConsentWindow() => InitializeComponent();

    public bool TelemetryEnabled => TelemetryCheck.IsChecked == true;

    private void AcceptCheck_OnChanged(object sender, RoutedEventArgs e) =>
        AcceptButton.IsEnabled = AcceptCheck.IsChecked == true;

    private void OpenTerms_OnClick(object sender, RoutedEventArgs e) =>
        new LegalDocumentWindow("用户协议", LegalDocuments.TermsText) { Owner = this }.ShowDialog();

    private void OpenPrivacy_OnClick(object sender, RoutedEventArgs e) =>
        new LegalDocumentWindow("隐私政策", LegalDocuments.PrivacyText) { Owner = this }.ShowDialog();

    private void Accept_OnClick(object sender, RoutedEventArgs e)
    {
        if (AcceptCheck.IsChecked != true)
        {
            return;
        }
        DialogResult = true;
    }

    private void Decline_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
