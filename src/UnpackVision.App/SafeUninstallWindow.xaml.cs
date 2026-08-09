using System.Windows;
using UnpackVision.Infrastructure;

namespace UnpackVision.App;

public partial class SafeUninstallWindow : Window
{
    public SafeUninstallWindow(LocalSettings settings)
    {
        InitializeComponent();
        LocalDataPathText.Text = UninstallCleanupService.DefaultLocalDataRoot;
        RecordingPathText.Text = settings.RecordingRoot;
        ExcelPathText.Text = string.IsNullOrWhiteSpace(settings.ExcelWorkbookPath)
            ? "未绑定 Excel"
            : settings.ExcelWorkbookPath;
    }

    public bool DeleteBusinessData { get; private set; }

    private void Cancel_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Continue_OnClick(object sender, RoutedEventArgs e)
    {
        DeleteBusinessData = DeleteDataOption.IsChecked == true;
        if (DeleteBusinessData)
        {
            var first = MessageBox.Show(
                this,
                "第一次警告：这会永久删除拆包智录的本机数据库、设置、配对信息以及当前工作区中的软件录像和截图。\n\nExcel 工作簿不会删除。是否继续？",
                "确认删除业务数据",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (first != MessageBoxResult.Yes) return;

            var second = MessageBox.Show(
                this,
                "最后警告：删除后无法通过回收站恢复。请确认重要录像已经备份。\n\n确定永久删除并卸载拆包智录吗？",
                "最后一次确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Stop,
                MessageBoxResult.No);
            if (second != MessageBoxResult.Yes) return;
        }
        else
        {
            var answer = MessageBox.Show(
                this,
                "将卸载拆包智录程序，但保留数据库、设置、录像和 Excel，是否继续？",
                "确认卸载",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }

        DialogResult = true;
        Close();
    }
}
