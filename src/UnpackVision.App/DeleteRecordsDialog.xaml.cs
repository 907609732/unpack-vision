using System.Windows;

namespace UnpackVision.App;

public partial class DeleteRecordsDialog : Window
{
    public DeleteRecordsDialog(int recordCount, bool hasExistingFiles)
    {
        InitializeComponent();
        DescriptionText.Text = $"将从全部记录中删除 {recordCount} 条数据，并取消这些记录尚未完成的同步任务。";
        DeleteFilesCheckBox.IsEnabled = hasExistingFiles;
        if (!hasExistingFiles)
        {
            DeleteFilesCheckBox.Content = "所选记录没有可删除的本地录像或截图";
        }
    }

    public bool DeleteFiles => DeleteFilesCheckBox.IsChecked == true;

    private void Delete_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
