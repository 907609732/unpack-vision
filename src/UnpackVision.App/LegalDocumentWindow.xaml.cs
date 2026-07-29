using System.Windows;

namespace UnpackVision.App;

public partial class LegalDocumentWindow : Window
{
    public LegalDocumentWindow(string title, string content)
    {
        InitializeComponent();
        Title = title;
        DocumentTitle.Text = title;
        DocumentText.Text = content;
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}
