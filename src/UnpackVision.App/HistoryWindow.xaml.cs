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
using UnpackVision.Infrastructure.Diagnostics;

namespace UnpackVision.App;

public partial class HistoryWindow : Window
{
    private const int SourcePageSize = 100;
    private const int TargetResultsPerLoad = 100;
    private const int MaximumSourceRowsPerLoad = 500;
    private readonly IScanRecordRepository _repository;
    private readonly LocalSettings _settings;
    private readonly ObservableCollection<RecentRecordingItem> _items = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _thumbnailLoadsSync = new();
    private readonly List<CancellationTokenSource> _thumbnailLoads = [];
    private int _nextOffset;
    private bool _hasMore = true;
    private bool _loading;
    private bool _deleting;
    private bool _closed;

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
        Loaded += async (_, _) => await LoadAsync(reset: true);
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            CancelThumbnailLoads();
        };
    }

    private async Task LoadAsync(bool reset)
    {
        if (_loading || _closed)
        {
            return;
        }

        _loading = true;
        SearchButton.IsEnabled = false;
        LoadMoreButton.IsEnabled = false;
        var startedAt = Stopwatch.GetTimestamp();
        var loadMode = reset ? "reset" : "append";
        using var operation = App.UiWatchdog?.BeginOperation("history.load");
        try
        {
            if (reset)
            {
                _nextOffset = 0;
                _hasMore = true;
                HistoryGrid.UnselectAll();
                _items.Clear();
                CancelThumbnailLoads();
            }
            ResultCountText.Text = reset ? "正在加载记录…" : $"正在继续加载，当前 {_items.Count} 条…";
            DiagnosticLog.Information("开始加载全部记录，方式 {HistoryLoadMode}", loadMode);

            var search = SearchInput.Text.Trim();
            var start = StartDatePicker.SelectedDate?.Date ?? DateTime.MinValue;
            var endExclusive = EndDatePicker.SelectedDate is null
                ? DateTime.MaxValue
                : EndDatePicker.SelectedDate.Value.Date.AddDays(1);
            var selectedTag = TagFilterSelector.SelectedItem as IssueTagDefinition;
            var onlyIssues = OnlyIssuesCheck.IsChecked == true;
            var filtered = new List<ScanRecord>();
            var scannedSourceRows = 0;

            while (_hasMore &&
                   scannedSourceRows < MaximumSourceRowsPerLoad &&
                   filtered.Count < TargetResultsPerLoad)
            {
                var page = await _repository.QueryPageAsync(
                    null,
                    _nextOffset,
                    SourcePageSize,
                    _lifetime.Token);
                scannedSourceRows += page.Count;
                _nextOffset += page.Count;
                _hasMore = page.Count == SourcePageSize;
                filtered.AddRange(page.Where(record =>
                    MatchesFilter(record, search, start, endExclusive, onlyIssues, selectedTag?.Id)));
                if (page.Count == 0)
                {
                    break;
                }
            }

            var deliveries = await _repository.GetLatestDeliveriesAsync(
                filtered.Select(record => record.Id).ToArray(),
                "excel",
                _lifetime.Token);
            var newItems = filtered
                .Select(record => RecentRecordingItem.CreateWithoutThumbnail(
                    record,
                    deliveries.GetValueOrDefault(record.Id)))
                .ToArray();
            foreach (var item in newItems)
            {
                _items.Add(item);
            }
            StartThumbnailLoading(newItems);

            ResultCountText.Text = _hasMore
                ? $"已加载 {_items.Count} 条结果，可继续加载"
                : $"共检索到 {_items.Count} 条结果";
            LoadMoreButton.Visibility = _hasMore ? Visibility.Visible : Visibility.Collapsed;
            UpdateSelectionState();
            DiagnosticLog.Information(
                "全部记录加载完成，方式 {HistoryLoadMode}，扫描 {SourceRowCount} 条，新增显示 {VisibleRowCount} 条，耗时 {ElapsedMilliseconds} 毫秒",
                loadMode,
                scannedSourceRows,
                newItems.Length,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Closing the window intentionally cancels database and thumbnail work.
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error(
                ex,
                "全部记录加载失败，方式 {HistoryLoadMode}，耗时 {ElapsedMilliseconds} 毫秒",
                loadMode,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            MessageBox.Show(this, ex.Message, "查询失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _loading = false;
            SearchButton.IsEnabled = !_closed;
            LoadMoreButton.IsEnabled = !_closed && _hasMore;
        }
    }

    private async void Search_OnClick(object sender, RoutedEventArgs e) =>
        await LoadAsync(reset: true);

    private async void LoadMore_OnClick(object sender, RoutedEventArgs e) =>
        await LoadAsync(reset: false);

    private async void SearchInput_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await LoadAsync(reset: true);
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
        try
        {
            WindowsFileLocation.SelectFile(item.Record.VideoPath);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error(exception, "从全部记录打开录像目录失败");
            MessageBox.Show(this, "无法打开录像所在目录，请查看诊断日志。", "无法打开",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenExcelFolder_OnClick(object sender, RoutedEventArgs e)
    {
        var location = WindowsFileLocation.Resolve(_settings.ExcelWorkbookPath);
        try
        {
            switch (location.State)
            {
                case WindowsFileLocationState.FileAvailable:
                    WindowsFileLocation.SelectFile(location.FullPath!);
                    DiagnosticLog.Information("用户从全部记录打开 Excel 工作簿目录，工作簿文件存在");
                    break;
                case WindowsFileLocationState.DirectoryAvailable:
                    WindowsFileLocation.OpenDirectory(location.DirectoryPath!);
                    DiagnosticLog.Warning("用户从全部记录打开 Excel 工作簿目录，但配置的工作簿文件不存在");
                    MessageBox.Show(this, "配置的 Excel 工作簿不存在，已打开它原本所在的文件夹。",
                        "工作簿不存在", MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;
                case WindowsFileLocationState.NotConfigured:
                    MessageBox.Show(this, "尚未配置 Excel 工作簿，请先在设置中选择工作簿。",
                        "尚未配置", MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
                case WindowsFileLocationState.InvalidPath:
                    MessageBox.Show(this, "Excel 工作簿路径无效，请在设置中重新选择工作簿。",
                        "路径无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;
                case WindowsFileLocationState.MissingDirectory:
                    MessageBox.Show(this, "Excel 工作簿所在文件夹不存在，请在设置中重新选择工作簿。",
                        "文件夹不存在", MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error(exception, "从全部记录打开 Excel 工作簿目录失败");
            MessageBox.Show(this, "无法打开 Excel 工作簿所在文件夹，请查看诊断日志。",
                "无法打开", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
            await LoadAsync(reset: true);
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

    private void SelectAllRowsCheckBox_OnClick(object sender, RoutedEventArgs e)
    {
        ToggleSelectAllLoadedRows();
        e.Handled = true;
    }

    private void SelectAll_OnClick(object sender, RoutedEventArgs e)
    {
        ToggleSelectAllLoadedRows();
    }

    private void ToggleSelectAllLoadedRows()
    {
        if (_items.Count > 0 && HistoryGrid.SelectedItems.Count == _items.Count)
        {
            HistoryGrid.UnselectAll();
        }
        else
        {
            HistoryGrid.SelectAll();
        }
        UpdateSelectionState();
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

            await LoadAsync(reset: true);
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
        SelectAllRowsCheckBox.IsChecked = selected == 0
            ? false
            : selected == _items.Count
                ? true
                : null;
    }

    private static bool MatchesFilter(
        ScanRecord record,
        string search,
        DateTime start,
        DateTime endExclusive,
        bool onlyIssues,
        string? selectedTagId) =>
        record.ScannedAt.LocalDateTime >= start &&
        record.ScannedAt.LocalDateTime < endExclusive &&
        (string.IsNullOrWhiteSpace(search) ||
         record.TrackingNo.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
         record.Note.Contains(search, StringComparison.CurrentCultureIgnoreCase)) &&
        (!onlyIssues || record.Tags.Any(tag => tag.IsActive)) &&
        (string.IsNullOrWhiteSpace(selectedTagId) ||
         record.Tags.Any(tag =>
             tag.IsActive &&
             string.Equals(tag.TagId, selectedTagId, StringComparison.OrdinalIgnoreCase)));

    private void StartThumbnailLoading(IReadOnlyList<RecentRecordingItem> items)
    {
        if (items.Count == 0 || _closed)
        {
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        lock (_thumbnailLoadsSync)
        {
            _thumbnailLoads.Add(cancellation);
        }
        _ = LoadThumbnailsAsync(items, cancellation);
    }

    private async Task LoadThumbnailsAsync(
        IReadOnlyList<RecentRecordingItem> items,
        CancellationTokenSource cancellation)
    {
        var completed = 0;
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            foreach (var item in items)
            {
                await item.LoadThumbnailAsync(cancellation.Token);
                completed++;
            }
            DiagnosticLog.Information(
                "全部记录缩略图后台加载完成，数量 {ThumbnailCount}，耗时 {ElapsedMilliseconds} 毫秒",
                completed,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            DiagnosticLog.Information(
                "全部记录缩略图后台加载已取消，已完成 {ThumbnailCount} 张",
                completed);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warning(
                ex,
                "全部记录缩略图后台加载失败，已完成 {ThumbnailCount} 张",
                completed);
        }
        finally
        {
            lock (_thumbnailLoadsSync)
            {
                _thumbnailLoads.Remove(cancellation);
            }
            cancellation.Dispose();
        }
    }

    private void CancelThumbnailLoads()
    {
        CancellationTokenSource[] active;
        lock (_thumbnailLoadsSync)
        {
            active = [.. _thumbnailLoads];
            _thumbnailLoads.Clear();
        }
        foreach (var cancellation in active)
        {
            cancellation.Cancel();
        }
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
