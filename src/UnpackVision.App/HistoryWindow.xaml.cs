using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using UnpackVision.Core;

namespace UnpackVision.App;

public partial class HistoryWindow : Window
{
    private readonly IScanRecordRepository _repository;
    private readonly ObservableCollection<RecentRecordingItem> _items = [];

    public HistoryWindow(IScanRecordRepository repository)
    {
        InitializeComponent();
        _repository = repository;
        HistoryGrid.ItemsSource = _items;
        StartDatePicker.SelectedDate = DateTime.Today.AddDays(-30);
        EndDatePicker.SelectedDate = DateTime.Today;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var records = await _repository.QueryAsync(SearchInput.Text, 300);
            var start = StartDatePicker.SelectedDate?.Date ?? DateTime.MinValue;
            var endExclusive = EndDatePicker.SelectedDate is null
                ? DateTime.MaxValue
                : EndDatePicker.SelectedDate.Value.Date.AddDays(1);
            var filtered = records.Where(record =>
                record.ScannedAt.LocalDateTime >= start && record.ScannedAt.LocalDateTime < endExclusive).ToArray();
            _items.Clear();
            foreach (var record in filtered)
            {
                var delivery = await _repository.GetDeliveryAsync(record.Id, "excel");
                _items.Add(await RecentRecordingItem.CreateAsync(record, delivery));
            }
            ResultCountText.Text = $"共检索到 {_items.Count} 条结果";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "查询失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Search_OnClick(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void SearchInput_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await LoadAsync();
        }
    }

    private void Play_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RecentRecordingItem item)
        {
            Play(item);
        }
    }

    private void OpenFolder_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RecentRecordingItem item ||
            string.IsNullOrWhiteSpace(item.Record.VideoPath) || !File.Exists(item.Record.VideoPath))
        {
            MessageBox.Show(this, "录像文件不存在。", "无法打开", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Record.VideoPath}\"") { UseShellExecute = true });
    }

    private async void RetrySync_OnClick(object sender, RoutedEventArgs e)
    {
        await _repository.RetryConnectorAsync("excel");
        MessageBox.Show(this, "失败或等待中的 Excel 同步任务已重新排队。", "重新同步", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ExportCsv_OnClick(object sender, RoutedEventArgs e)
    {
        var selected = HistoryGrid.SelectedItems.Cast<RecentRecordingItem>().ToArray();
        var exportItems = selected.Length == 0 ? _items.ToArray() : selected;
        if (exportItems.Length == 0)
        {
            return;
        }
        var dialog = new SaveFileDialog
        {
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"拆包录像记录_{DateTime.Now:yyyyMMddHHmmss}.csv"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        var csv = new StringBuilder("快递单号,模式,开始时间,结束时间,时长,状态,录像路径\r\n");
        foreach (var item in exportItems)
        {
            csv.AppendLine(string.Join(',',
                Csv(item.TrackingNo),
                Csv(item.Record.Workflow.ToString()),
                Csv(item.Record.RecordingStartedAt?.ToString("O") ?? ""),
                Csv(item.Record.RecordingEndedAt?.ToString("O") ?? ""),
                Csv(item.DurationText),
                Csv(item.StatusText),
                Csv(item.Record.VideoPath ?? "")));
        }
        File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(true));
    }

    private void ExportVideos_OnClick(object sender, RoutedEventArgs e)
    {
        var selected = HistoryGrid.SelectedItems.Cast<RecentRecordingItem>()
            .Where(item => !string.IsNullOrWhiteSpace(item.Record.VideoPath) && File.Exists(item.Record.VideoPath))
            .ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, "请先选择需要导出的录像。", "导出录像", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new OpenFolderDialog { Title = "选择录像导出目录" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        foreach (var item in selected)
        {
            var source = item.Record.VideoPath!;
            var destination = GetAvailablePath(dialog.FolderName, Path.GetFileName(source));
            File.Copy(source, destination, overwrite: false);
        }
        MessageBox.Show(this, $"已导出 {selected.Length} 个录像文件。", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Play(RecentRecordingItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Record.VideoPath) || !File.Exists(item.Record.VideoPath))
        {
            MessageBox.Show(this, "录像文件不存在。", "无法播放", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        new VideoPlayerWindow(item.Record.VideoPath) { Owner = this }.Show();
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private static string GetAvailablePath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var number = 2; ; number++)
        {
            candidate = Path.Combine(directory, $"{baseName}_{number}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
