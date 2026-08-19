using System.Xml.Linq;
using UnpackVision.App;
using UnpackVision.Core;

namespace UnpackVision.Tests;

public sealed class PhotoGalleryContractTests
{
    [Fact]
    public void MainAndHistoryWindowsExposePhotoActions()
    {
        var main = LoadXaml("MainWindow.xaml");
        var history = LoadXaml("HistoryWindow.xaml");

        Assert.Contains(main.Descendants(), element =>
            (string?)element.Attribute("Click") == "ViewSnapshotsButton_OnClick");
        Assert.Contains(main.Descendants(), element =>
            (string?)element.Attribute("Click") == "RecentPhotosButton_OnClick");
        Assert.Contains(history.Descendants(), element =>
            (string?)element.Attribute("Click") == "Photos_OnClick");
    }

    [Fact]
    public void GalleryProvidesPreviewNavigationAndFileActions()
    {
        var gallery = LoadXaml("PhotoGalleryWindow.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        foreach (var name in new[] { "PhotoList", "PreviewImage", "PreviousButton", "NextButton", "PhotoPathText" })
        {
            Assert.Contains(gallery.Descendants(), element => (string?)element.Attribute(x + "Name") == name);
        }
        Assert.Contains(gallery.Descendants(), element => (string?)element.Attribute("Click") == "OpenImage_OnClick");
        Assert.Contains(gallery.Descendants(), element => (string?)element.Attribute("Click") == "OpenFolder_OnClick");
    }

    [Fact]
    public void RecentItemCountsOnlyExistingDistinctPhotos()
    {
        var root = Path.Combine(Path.GetTempPath(), $"unpackvision-photo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var first = Path.Combine(root, "first.jpg");
            var second = Path.Combine(root, "second.jpg");
            File.WriteAllBytes(first, [1]);
            File.WriteAllBytes(second, [2]);
            var item = RecentRecordingItem.CreateWithoutThumbnail(new ScanRecord
            {
                TrackingNo = "TEST00000001",
                Snapshots = [first, first, second, Path.Combine(root, "missing.jpg")]
            });

            Assert.True(item.HasSnapshots);
            Assert.Equal(2, item.SnapshotCount);
            Assert.Equal("照片 2", item.SnapshotCountText);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static XDocument LoadXaml(string name) => XDocument.Load(
        Path.Combine(AppContext.BaseDirectory, "TestData", name));
}
