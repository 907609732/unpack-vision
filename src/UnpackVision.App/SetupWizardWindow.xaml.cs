using System.IO;
using System.Windows;
using Microsoft.Win32;
using UnpackVision.Core;
using UnpackVision.Infrastructure;

namespace UnpackVision.App;

public partial class SetupWizardWindow : Window
{
    private readonly LocalSettings _settings;
    private readonly IWorkbookTemplateService _workbookService = new WorkbookTemplateService();
    private int _step;
    private bool _excelSkipped;
    private RecoveryPreview? _preview;

    public SetupWizardWindow(LocalSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        RecordingRootInput.Text = settings.RecordingRoot;
        ExcelPathInput.Text = settings.ExcelWorkbookPath;
        _excelSkipped = settings.Setup.ExcelSkipped;
        RenderStep();
    }

    public LocalSettings? SavedSettings { get; private set; }

    private async void Next_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_step == 1)
            {
                await ValidateStorageAsync();
            }
            if (_step == 2 && !_excelSkipped && !string.IsNullOrWhiteSpace(ExcelPathInput.Text))
            {
                var validation = await _workbookService.ValidateAsync(ExcelPathInput.Text);
                if (!validation.Valid)
                {
                    throw new InvalidOperationException(validation.Message);
                }
            }
            if (_step == 2)
            {
                await LoadRecoveryPreviewAsync();
            }
            if (_step == 3)
            {
                await ApplyRecoveryAsync();
            }
            if (_step == 4)
            {
                SavedSettings = _settings;
                DialogResult = true;
                return;
            }
            _step++;
            RenderStep();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "无法继续", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Back_OnClick(object sender, RoutedEventArgs e)
    {
        if (_step <= 0)
        {
            return;
        }
        _step--;
        RenderStep();
    }

    private void Exit_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void BrowseRecordingRoot_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择录像保存目录",
            InitialDirectory = RecordingRootInput.Text
        };
        if (dialog.ShowDialog(this) == true)
        {
            RecordingRootInput.Text = dialog.FolderName;
            StorageStatusText.Text = "已选择目录，点击“下一步”时将再次验证。";
        }
    }

    private async void CheckStorage_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ValidateStorageAsync();
        }
        catch (Exception ex)
        {
            StorageStatusText.Text = ex.Message;
        }
    }

    private async Task ValidateStorageAsync()
    {
        var path = RecordingRootInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("请选择录像保存目录");
        }
        var fullPath = Path.GetFullPath(path);
        if (fullPath.Length > 170)
        {
            throw new PathTooLongException("录像目录过深，请选择更靠近磁盘根目录的位置");
        }
        Directory.CreateDirectory(fullPath);
        var probe = Path.Combine(fullPath, $".unpackvision-write-test-{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(probe, "ok");
        File.Delete(probe);
        var drive = new DriveInfo(Path.GetPathRoot(fullPath)!);
        StorageStatusText.Text =
            $"目录可写 · 可用空间 {drive.AvailableFreeSpace / 1024D / 1024D / 1024D:0.0} GB";
        _settings.RecordingRoot = fullPath;
        var catalog = new PortableRecordCatalog(fullPath);
        var manifest = await catalog.EnsureWorkspaceAsync(
            _settings.Setup.WorkspaceId == Guid.Empty ? null : _settings.Setup.WorkspaceId);
        _settings.Setup.WorkspaceId = manifest.WorkspaceId;
    }

    private async void BrowseExcel_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Excel 工作簿",
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx"
        };
        if (dialog.ShowDialog(this) == true)
        {
            ExcelPathInput.Text = dialog.FileName;
            _excelSkipped = false;
            var result = await _workbookService.ValidateAsync(dialog.FileName);
            ExcelStatusText.Text = result.Message;
        }
    }

    private async void CreateExcel_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "生成标准退货扫码表格",
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
            FileName = $"退货扫码记录_{DateTime.Now:yyyy年MM月}.xlsx",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        try
        {
            await _workbookService.CreateAsync(dialog.FileName);
            ExcelPathInput.Text = dialog.FileName;
            ExcelStatusText.Text = "标准六列表格已生成并通过检查";
            _excelSkipped = false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "生成失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SkipExcel_OnClick(object sender, RoutedEventArgs e)
    {
        ExcelPathInput.Clear();
        _excelSkipped = true;
        ExcelStatusText.Text = "已选择暂时跳过；可以随时在设置中绑定或生成表格。";
    }

    private async Task LoadRecoveryPreviewAsync()
    {
        _settings.ExcelWorkbookPath = _excelSkipped ? string.Empty : ExcelPathInput.Text.Trim();
        var storage = new StorageOptions { RecordingRoot = _settings.RecordingRoot };
        var repository = new SqliteScanRecordRepository(storage);
        await repository.InitializeAsync();
        var recovery = new WorkspaceRecoveryService(repository, storage);
        _preview = await recovery.PreviewAsync(
            _settings.RecordingRoot,
            _settings.ExcelWorkbookPath);
        RecoveryItemsList.ItemsSource = _preview.Items.Take(200);
        RecoverySummaryText.Text =
            $"完整 {_preview.CompleteCount} · 仅文件名 {_preview.FileNameOnlyCount} · " +
            $"Excel关联 {_preview.ExcelMatchedCount} · 冲突 {_preview.ConflictCount} · " +
            $"录像缺失 {_preview.MissingVideoCount} · 无效 {_preview.InvalidCount}";
        RecoverDataCheck.IsChecked =
            _preview.Items.Any(item => item.Record is not null);
    }

    private async Task ApplyRecoveryAsync()
    {
        _settings.Setup.Version = SetupState.CurrentVersion;
        _settings.Setup.CompletedAt = DateTimeOffset.Now;
        _settings.Setup.ExcelSkipped = _excelSkipped;
        if (_preview is not null && RecoverDataCheck.IsChecked == true)
        {
            var storage = new StorageOptions { RecordingRoot = _settings.RecordingRoot };
            var repository = new SqliteScanRecordRepository(storage);
            await repository.InitializeAsync();
            var recovery = new WorkspaceRecoveryService(repository, storage);
            var result = await recovery.RecoverAsync(_preview);
            FinishRecoveryText.Text =
                $"恢复完成：新增 {result.Added}、更新 {result.Updated}、跳过 {result.Skipped}。";
        }
        else
        {
            FinishRecoveryText.Text = "没有合并历史记录。以后可在设置中重新检查。";
        }
        FinishSummaryText.Text =
            $"录像目录：{_settings.RecordingRoot}\n" +
            $"Excel：{(string.IsNullOrWhiteSpace(_settings.ExcelWorkbookPath) ? "暂未绑定" : _settings.ExcelWorkbookPath)}\n" +
            $"工作区：{_settings.Setup.WorkspaceId:D}";
    }

    private void RenderStep()
    {
        WelcomePanel.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        StoragePanel.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        ExcelPanel.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        RecoveryPanel.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        FinishPanel.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;
        var titles = new[]
        {
            ("欢迎使用", "先了解数据如何保存和迁移。"),
            ("设置录像目录", "录像与便携索引将一起保存在这里。"),
            ("绑定 Excel", "选择现有表格、生成标准表格或暂时跳过。"),
            ("检查历史数据", "扫描旧录像、便携索引和 Excel 同步标记。"),
            ("一切就绪", "确认后直接进入软件主页。")
        };
        StepTitle.Text = titles[_step].Item1;
        StepDescription.Text = titles[_step].Item2;
        StepProgress.Value = _step + 1;
        BackButton.IsEnabled = _step > 0;
        NextButton.Content = _step == 4 ? "进入软件" : "下一步";
        var labels = new[] { Step1Label, Step2Label, Step3Label, Step4Label, Step5Label };
        for (var index = 0; index < labels.Length; index++)
        {
            labels[index].FontWeight = index == _step ? FontWeights.SemiBold : FontWeights.Normal;
            labels[index].Foreground = index == _step
                ? (System.Windows.Media.Brush)FindResource("PrimaryBrush")
                : (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
        }
    }
}
