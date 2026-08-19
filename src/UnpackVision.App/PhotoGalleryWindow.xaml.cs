using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UnpackVision.Infrastructure;
using UnpackVision.Infrastructure.Diagnostics;

namespace UnpackVision.App;

public partial class PhotoGalleryWindow : Window
{
    private readonly IReadOnlyList<PhotoGalleryItem> _photos;

    public PhotoGalleryWindow(string trackingNo, IEnumerable<string> snapshotPaths)
    {
        InitializeComponent();
        var paths = snapshotPaths
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _photos = paths
            .Select((path, index) => PhotoGalleryItem.Create(path, index + 1))
            .ToArray();
        PhotoList.ItemsSource = _photos;
        GalleryTitleText.Text = string.IsNullOrWhiteSpace(trackingNo) ? "拍照记录" : $"拍照记录 · {trackingNo}";
        GallerySummaryText.Text = $"共 {_photos.Count} 张 · 双击大图可用系统图片应用打开原图";
        if (_photos.Count > 0)
        {
            PhotoList.SelectedIndex = 0;
        }
    }

    public static bool TryShow(Window owner, string trackingNo, IEnumerable<string> snapshotPaths)
    {
        var existing = snapshotPaths
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (existing.Length == 0)
        {
            MessageBox.Show(owner, "这条记录没有可用照片，照片可能尚未拍摄、已移动或已删除。",
                "没有照片", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        new PhotoGalleryWindow(trackingNo, existing) { Owner = owner }.Show();
        return true;
    }

    private PhotoGalleryItem? SelectedPhoto => PhotoList.SelectedItem as PhotoGalleryItem;

    private void PhotoList_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SelectedPhoto is not { } photo)
        {
            PreviewImage.Source = null;
            return;
        }
        PreviewImage.Source = LoadImage(photo.Path, 1800);
        PhotoNameText.Text = photo.FileName;
        PhotoPathText.Text = photo.Path;
        PreviousButton.IsEnabled = PhotoList.SelectedIndex > 0;
        NextButton.IsEnabled = PhotoList.SelectedIndex >= 0 && PhotoList.SelectedIndex < _photos.Count - 1;
    }

    private void Previous_OnClick(object sender, RoutedEventArgs e)
    {
        if (PhotoList.SelectedIndex > 0) PhotoList.SelectedIndex--;
    }

    private void Next_OnClick(object sender, RoutedEventArgs e)
    {
        if (PhotoList.SelectedIndex < _photos.Count - 1) PhotoList.SelectedIndex++;
    }

    private void PreviewImage_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) OpenSelectedImage();
    }

    private void OpenImage_OnClick(object sender, RoutedEventArgs e) => OpenSelectedImage();

    private void OpenFolder_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedPhoto is not { } photo) return;
        try
        {
            WindowsFileLocation.SelectFile(photo.Path);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error(exception, "打开照片所在目录失败");
            MessageBox.Show(this, "无法打开照片所在目录，请查看诊断日志。", "无法打开",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenSelectedImage()
    {
        if (SelectedPhoto is not { } photo) return;
        try
        {
            Process.Start(new ProcessStartInfo(photo.Path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error(exception, "使用系统图片应用打开照片失败");
            MessageBox.Show(this, "无法打开原图，请查看诊断日志。", "无法打开",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static ImageSource? LoadImage(string path, int decodePixelWidth)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = decodePixelWidth;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            DiagnosticLog.Warning(exception, "读取照片预览失败");
            return null;
        }
    }

    private sealed record PhotoGalleryItem(
        string Path,
        string FileName,
        string NumberText,
        string CapturedAtText,
        ImageSource? Thumbnail)
    {
        public static PhotoGalleryItem Create(string path, int number)
        {
            var file = new FileInfo(path);
            return new PhotoGalleryItem(
                path,
                file.Name,
                $"照片 {number}",
                file.LastWriteTime.ToString("yyyy/MM/dd HH:mm:ss"),
                LoadImage(path, 180));
        }
    }
}
