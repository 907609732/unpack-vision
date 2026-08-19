using System.ComponentModel;
using System.Windows;
using UnpackVision.Core;

namespace UnpackVision.App;

public partial class LegacyMigrationRepairProgressWindow : Window
{
    private bool _canClose;

    public LegacyMigrationRepairProgressWindow()
    {
        InitializeComponent();
    }

    public void Report(RecordingRootMigrationProgress progress)
    {
        var percent = progress.TotalBytes <= 0
            ? progress.TotalFiles <= 0 ? 0 : progress.CompletedFiles * 100d / progress.TotalFiles
            : progress.CompletedBytes * 100d / progress.TotalBytes;
        RepairProgressBar.Value = Math.Clamp(percent, 0, 100);
        PercentText.Text = $"{RepairProgressBar.Value:0}%";
        StageText.Text = "正在复制、复核并清理旧副本";
        DetailText.Text = string.IsNullOrWhiteSpace(progress.RelativePath)
            ? $"已处理 {progress.CompletedFiles:N0}/{progress.TotalFiles:N0} 个文件"
            : $"{progress.CompletedFiles:N0}/{progress.TotalFiles:N0} · {progress.RelativePath}";
    }

    public void Complete(bool success, string detail)
    {
        _canClose = true;
        TitleText.Text = success ? "旧录像迁移已修复" : "旧录像迁移未完成";
        StageText.Text = success ? "安全修复完成" : "未删除无法确认的文件";
        DetailText.Text = success
            ? "数据库路径和旧录像副本已经按校验结果处理。"
            : "新位置仍然有效；请查看下面的原因后重试。";
        ResultText.Text = detail;
        ResultText.Visibility = Visibility.Visible;
        CloseButton.Visibility = Visibility.Visible;
        if (success)
        {
            RepairProgressBar.Value = 100;
            PercentText.Text = "100%";
        }
        else
        {
            PercentText.Text = "未完成";
        }
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_canClose)
        {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();
}
