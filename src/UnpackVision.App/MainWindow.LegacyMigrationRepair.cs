using System.Windows;
using UnpackVision.Core;
using UnpackVision.Infrastructure;

namespace UnpackVision.App;

public partial class MainWindow
{
    private int _legacyMigrationRepairOfferStarted;

    private void ScheduleLegacyMigrationRepairOffer()
    {
        _ = Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            () => _ = OfferLegacyMigrationRepairAsync());
    }

    private async Task OfferLegacyMigrationRepairAsync()
    {
        if (Interlocked.Exchange(ref _legacyMigrationRepairOfferStarted, 1) != 0 ||
            _repository is null || _storageOptions is null || _lifetime.IsCancellationRequested)
        {
            return;
        }

        try
        {
            var service = new LegacyRecordingMigrationRepairService(_repository, _storageOptions);
            var candidate = await service.FindPendingAsync(_settings.RecordingRoot, _lifetime.Token);
            if (candidate is null)
            {
                return;
            }
            if (_coordinator?.State != RecordingState.Idle)
            {
                FooterText.Text = "检测到旧版迁移留下的源副本；下次空闲启动时可安全清理";
                return;
            }

            var choice = MessageBox.Show(
                this,
                $"检测到旧版本完成过录像目录迁移，但旧位置仍保留了 {candidate.FileCount:N0} 个录像相关文件" +
                $"（约 {FormatBytes(candidate.TotalBytes)}）。\n\n" +
                $"旧位置：{candidate.SourceRoot}\n新位置：{candidate.TargetRoot}\n\n" +
                "是否现在修复？软件会重新逐个计算 SHA-256、备份数据库并更新旧路径，" +
                "只删除与新位置完全一致的源副本。未完成录像、无关文件、缺失或内容不同的文件都会保留。",
                "发现可安全清理的旧录像副本",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (choice != MessageBoxResult.Yes)
            {
                FooterText.Text = "已保留旧录像副本，可在下次启动时再次处理";
                return;
            }

            // This existing station-wide gate also rejects mobile commands. It prevents a
            // new recording from racing the database path transaction and source cleanup.
            _cameraConfigurationPreviewActive = true;
            var progressWindow = new LegacyMigrationRepairProgressWindow { Owner = this };
            var progress = new Progress<RecordingRootMigrationProgress>(progressWindow.Report);
            var operation = RunLegacyMigrationRepairAsync(service, candidate, progressWindow, progress);
            progressWindow.ShowDialog();
            var result = await operation;

            await RefreshRecentAsync();
            FooterText.Text = result.Cleanup.IsComplete
                ? $"旧录像迁移已修复：更新 {result.RebasedPaths:N0} 个数据库路径，清理 {result.Cleanup.DeletedFiles:N0} 个旧副本"
                : $"旧录像迁移已修复，另有 {result.Cleanup.RemainingFiles.Count:N0} 个无法确认的文件已保留";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            FooterText.Text = "旧录像迁移修复未完成，源文件未被强制删除";
            MessageBox.Show(
                this,
                "安全修复未完成。新录像位置和无法确认的旧文件均已保留。\n\n" + exception.Message,
                "旧录像迁移修复未完成",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _cameraConfigurationPreviewActive = false;
        }
    }

    private static async Task<LegacyRecordingMigrationRepairResult> RunLegacyMigrationRepairAsync(
        LegacyRecordingMigrationRepairService service,
        LegacyRecordingMigrationRepairCandidate candidate,
        LegacyMigrationRepairProgressWindow window,
        IProgress<RecordingRootMigrationProgress> progress)
    {
        try
        {
            var result = await service.RepairAsync(candidate, progress, CancellationToken.None);
            var detail = result.Cleanup.IsComplete
                ? $"已更新 {result.RebasedPaths:N0} 个数据库路径，并安全移除 {result.Cleanup.DeletedFiles:N0} 个旧副本。\n" +
                  $"校验报告：{result.Cleanup.ReportPath}"
                : $"已更新 {result.RebasedPaths:N0} 个数据库路径并清理 {result.Cleanup.DeletedFiles:N0} 个旧副本；" +
                  $"另有 {result.Cleanup.RemainingFiles.Count:N0} 个文件因无法确认而保留。\n" +
                  $"详细报告：{result.Cleanup.ReportPath}";
            window.Complete(result.Cleanup.IsComplete, detail);
            return result;
        }
        catch (Exception exception)
        {
            window.Complete(false, exception.Message);
            throw;
        }
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? $"{bytes / 1024d / 1024d / 1024d:F2} GB"
            : $"{bytes / 1024d / 1024d:F1} MB";
}
