using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using UnpackVision.Core;

namespace UnpackVision.App;

/// <summary>
/// Keeps a hardware test visibly separate from the settings form. The window cannot be
/// dismissed while a camera is owned by the test backend, which avoids leaving users with
/// an apparently frozen settings page or an indeterminate capture lifetime.
/// </summary>
public partial class CameraTestProgressWindow : Window
{
    private bool _canClose;

    public CameraTestProgressWindow(string title)
    {
        InitializeComponent();
        TitleText.Text = title;
    }

    public void Report(CameraPerformanceProgress progress, string? cameraName = null)
    {
        var percent = Math.Clamp(progress.Percent, 0, 100);
        TestProgressBar.Value = percent;
        PercentText.Text = $"{percent}%";
        StageText.Text = string.IsNullOrWhiteSpace(cameraName)
            ? progress.Stage
            : $"{cameraName} · {progress.Stage}";
        RemainingText.Text = progress.SecondsRemaining is > 0
            ? $"本阶段预计还需 {progress.SecondsRemaining} 秒，请保持摄像头连接"
            : percent >= 100
                ? "测试数据已采集完成，正在整理结果…"
                : "正在切换到下一测试阶段…";
    }

    public void Complete(bool success, string title, string detail)
    {
        _canClose = true;
        TitleText.Text = title;
        StageText.Text = success ? "测试完成" : "测试未通过";
        RemainingText.Text = success ? "当前机位配置可以正常使用。" : "请根据结果调整配置后重新测试。";
        ResultText.Text = detail;
        ResultText.Visibility = Visibility.Visible;
        CloseButton.Visibility = Visibility.Visible;
        TestProgressBar.Value = 100;
        PercentText.Text = success ? "100%" : "未通过";
        StatusIconBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(success ? "#EAF8F0" : "#FFF1F0"));
        StatusIconText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(success ? "#1C9B55" : "#D92D20"));
        StatusIconText.Text = success ? "\uE73E" : "\uEA39";
        Activate();
    }

    public void CloseFinished()
    {
        _canClose = true;
        Close();
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

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => CloseFinished();
}
