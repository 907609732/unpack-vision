using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UnpackVision.Core;
using UnpackVision.Infrastructure;

namespace UnpackVision.App;

public sealed class RecentRecordingItem
{
    public required ScanRecord Record { get; init; }
    public SyncDelivery? ExcelDelivery { get; init; }
    public ImageSource? Thumbnail { get; init; }
    public string TrackingNo => Record.TrackingNo;
    public string TimeText => Record.RecordingStartedAt?.ToString("yyyy/MM/dd HH:mm:ss") ?? Record.ScannedAt.ToString("yyyy/MM/dd HH:mm:ss");
    public string DurationText
    {
        get
        {
            if (Record.RecordingStartedAt is null || Record.RecordingEndedAt is null)
            {
                return "--:--";
            }
            var duration = Record.RecordingEndedAt.Value - Record.RecordingStartedAt.Value;
            return duration.TotalHours >= 1 ? duration.ToString("hh\\:mm\\:ss") : duration.ToString("mm\\:ss");
        }
    }
    public string FileName => string.IsNullOrWhiteSpace(Record.VideoPath) ? "尚未生成录像文件" : Path.GetFileName(Record.VideoPath);
    public string FileSizeText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Record.VideoPath) || !File.Exists(Record.VideoPath))
            {
                return Record.State == RecordingState.Failed ? "异常" : "--";
            }
            var bytes = new FileInfo(Record.VideoPath).Length;
            return bytes >= 1024L * 1024L * 1024L
                ? $"{bytes / 1024d / 1024d / 1024d:F1} GB"
                : $"{Math.Max(0.1, bytes / 1024d / 1024d):F1} MB";
        }
    }
    public string StatusText => Record.State switch
    {
        RecordingState.Completed or RecordingState.Imported => ExcelDelivery?.Status switch
        {
            SyncStatus.Succeeded => "Excel 已同步",
            SyncStatus.Processing => "Excel 同步中",
            SyncStatus.Failed => "Excel 同步失败",
            SyncStatus.Pending => "Excel 等待同步",
            _ => Record.State == RecordingState.Imported ? "HIK 历史基线" : "已保存"
        },
        RecordingState.Failed => "异常",
        RecordingState.Recording => "录制中",
        _ => Record.State.ToString()
    };
    public bool IsDuplicate => Record.DuplicateOf is not null;

    public static async Task<RecentRecordingItem> CreateAsync(ScanRecord record, SyncDelivery? excelDelivery = null)
    {
        var bytes = await Task.Run(() => VideoPresentationService.CreateThumbnailJpeg(record.VideoPath));
        return new RecentRecordingItem
        {
            Record = record,
            ExcelDelivery = excelDelivery,
            Thumbnail = bytes is null ? null : UiImage.FromBytes(bytes)
        };
    }
}

internal static class UiImage
{
    public static BitmapImage FromBytes(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
