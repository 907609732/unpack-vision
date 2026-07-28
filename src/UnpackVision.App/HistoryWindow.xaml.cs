using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Microsoft.VisualBasic.FileIO;
using UnpackVision.Core;
using UnpackVision.Infrastructure;

namespace UnpackVision.App;

public partial class HistoryWindow : Window
{
    private readonly IScanRecordRepository _repository;
    private readonly LocalSettings _settings;
    private readonly ObservableCollection<RecentRecordingItem> _items = [];
    private bool _deleting;

    public HistoryWindow(IScanRecordRepository repository, LocalSettings settings)
    {
        InitializeComponent();
        _repository = repository;
        _settings = settings;
        HistoryGrid.ItemsSource = _items;
        TagFilterSelector.ItemsSource = new[] { new IssueTagDefinition { Id = string.Empty, Name = "全部标签" } }
            .Concat(settings.IssueTags.Where(item => item.Enabled).OrderBy(item => item.SortOrder)).ToArray();
        TagFilterSelector.SelectedIndex = 0;
        StartDatePicker.SelectedDate = DateTime.Today.AddDays(-30);
        EndDatePicker.SelectedDate = DateTime.Today;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var search = SearchInput.Text.Trim();
            var records = await _repository.QueryAsync(limit: 500);
            var start = StartDatePicker.SelectedDate?.Date ?? DateTime.MinValue;
            var endExclusive = EndDatePicker.SelectedDate is null
                ? DateTime.MaxValue
                : EndDatePicker.SelectedDate.Value.Date.AddDays(1);
            var selectedTag = TagFilterSelector.SelectedItem as IssueTagDefinition;
            var filtered = records.Where(record =>
                record.ScannedAt.LocalDateTime >= start && record.ScannedAt.LocalDateTime < endExclusive &&
                (string.IsNullOrWhiteSpace(search) || record.TrackingNo.Contains(search, StringComparison.CurrentCultureIgnoreCase) || record.Note.Contains(search, StringComparison.CurrentCultureIgnoreCase)) &&
                (OnlyIssuesCheck.IsChecked != true || record.Tags.Any(tag => tag.IsActive)) &&
                (string.IsNullOrWhiteSpace(selectedTag?.Id) || record.Tags.Any(tag => tag.IsActive && string.Equals(tag.TagId, selectedTag.Id, StringComparison.OrdinalIgnoreCase))))
                .ToArray();
            _items.Clear();
            foreach (var record in filtered)
            {
                var delivery = await _repository.GetDeliveryAsync(record.Id, "excel");
                _items.Add(await RecentRecordingItem.CreateAsync(record, delivery));
            }
            ResultCountText.Text = $"共检索到 {_items.Count} 条结果";
            UpdateSelectionState();
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

    private async void EditAnnotation_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RecentRecordingItem item) return;
        var dialog = new RecordAnnotationDialog(item.Record, _settings.IssueTags) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var now = DateTimeOffset.Now;
            var active = (await _repository.GetTagsAsync(item.Record.Id)).ToArray();
            var selectedIds = dialog.Tags.Where(tag => tag.Selected).Select(tag => tag.Definition.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var assignment in active.Where(tag => !selectedIds.Contains(tag.TagId)))
            {
                await _repository.RemoveTagAsync(item.Record.Id, assignment.Id, now);
            }
            foreach (var tag in dialog.Tags.Where(tag => tag.Selected && active.All(assignment => !string.Equals(assignment.TagId, tag.Definition.Id, StringComparison.OrdinalIgnoreCase))))
            {
                await _repository.AddTagAsync(item.Record.Id, tag.Definition, now, "history");
            }
            await _repository.UpdateNoteAsync(item.Record.Id, dialog.Note, now);
            var updated = await _repository.GetAsync(item.Record.Id) ?? throw new InvalidOperationException("记录不存在。");
            try
            {
                var renamed = RecordingFileRenameService.TryRenameLocalRecording(updated, _settings.RecordingRoot);
                if (!string.IsNullOrWhiteSpace(renamed) && !string.Equals(renamed, updated.VideoPath, StringComparison.OrdinalIgnoreCase))
                {
                    updated.VideoPath = renamed;
                    updated.UpdatedAt = now;
                    await _repository.UpdateAsync(updated);
                }
                await _repository.SetMetadataAsync($"video-rename:{updated.Id:D}", string.Empty);
            }
            catch (IOException ex)
            {
                await _repository.SetMetadataAsync($"video-rename:{updated.Id:D}", ex.Message);
            }
            if (updated.State is RecordingState.Completed or RecordingState.Imported)
            {
                await _repository.EnqueueDeliveryAsync(updated.Id, "excel");
            }
            await LoadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "保存异常与备注失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RetrySync_OnClick(object sender, RoutedEventArgs e)
    {
        await _repository.RetryConnectorAsync("excel");
        MessageBox.Show(this, "失败或等待中的 Excel 同步任务已重新排队。", "重新同步", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void HistoryGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionState();

    private void SelectAll_OnClick(object sender, RoutedEventArgs e)
    {
        if (_items.Count > 0 && HistoryGrid.SelectedItems.Count == _items.Count)
        {
            HistoryGrid.UnselectAll();
        }
        else
        {
            HistoryGrid.SelectAll();
        }
    }

    private async void DeleteSelected_OnClick(object sender, RoutedEventArgs e) => await DeleteSelectedAsync();

    private async void HistoryGrid_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && DeleteSelectedButton.IsEnabled)
        {
            e.Handled = true;
            await DeleteSelectedAsync();
        }
    }

    private async Task DeleteSelectedAsync()
    {
        if (_deleting)
        {
            return;
        }

        var selected = HistoryGrid.SelectedItems.Cast<RecentRecordingItem>().ToArray();
        if (selected.Length == 0)
        {
            return;
        }
        if (selected.Any(item => item.Record.State is RecordingState.Starting or RecordingState.Recording or RecordingState.Saving))
        {
            MessageBox.Show(this, "所选内容包含正在录制或保存的记录，请等待录像完成后再删除。", "无法删除",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var files = selected
            .SelectMany(item => EnumerateRecordFiles(item.Record))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var dialog = new DeleteRecordsDialog(selected.Length, files.Length > 0) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _deleting = true;
        DeleteSelectedButton.IsEnabled = false;
        try
        {
            var deleted = await _repository.DeleteManyAsync(selected.Select(item => item.Record.Id).ToArray());
            var failedFiles = new List<string>();
            var recycled = 0;
            if (dialog.DeleteFiles)
            {
                foreach (var path in files)
                {
                    try
                    {
                        FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                        recycled++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
                    {
                        failedFiles.Add(path);
                    }
                }
            }

            await LoadAsync();
            var message = $"已删除 {deleted} 条记录。";
            if (dialog.DeleteFiles)
            {
                message += $"\n已将 {recycled} 个录像或截图移到回收站。";
            }
            if (failedFiles.Count > 0)
            {
                message += $"\n有 {failedFiles.Count} 个文件未能移动，仍保留在原位置。";
            }
            MessageBox.Show(this, message, "删除完成", MessageBoxButton.OK,
                failedFiles.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "删除失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _deleting = false;
            UpdateSelectionState();
        }
    }

    private void UpdateSelectionState()
    {
        var selected = HistoryGrid.SelectedItems.Count;
        SelectionCountText.Text = selected == 0 ? "未选择记录" : $"已选择 {selected} 条";
        DeleteSelectedButton.IsEnabled = !_deleting && selected > 0;
        SelectAllButton.Content = _items.Count > 0 && selected == _items.Count ? "取消全选" : "全选";
    }

    private static IEnumerable<string> EnumerateRecordFiles(ScanRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.VideoPath))
        {
            yield return record.VideoPath;
        }
        foreach (var snapshot in record.Snapshots)
        {
            if (!string.IsNullOrWhiteSpace(snapshot))
            {
                yield return snapshot;
            }
        }
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
        var csv = new StringBuilder("快递单号,模式,开始时间,结束时间,时长,异常标签,备注,状态,录像路径\r\n");
        foreach (var item in exportItems)
        {
            csv.AppendLine(string.Join(',',
                Csv(item.TrackingNo),
                Csv(item.Record.Workflow.ToString()),
                Csv(item.Record.RecordingStartedAt?.ToString("O") ?? ""),
                Csv(item.Record.RecordingEndedAt?.ToString("O") ?? ""),
                Csv(item.DurationText),
                Csv(item.TagSummary),
                Csv(item.Record.Note),
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
        new VideoPlayerWindow(item.Record) { Owner = this }.Show();
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
