using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media.Imaging;
using UnpackVision.Core;

namespace UnpackVision.App;

public partial class DonationWindow : Window
{
    public DonationWindow(DonationProfile profile)
    {
        InitializeComponent();
        LoadQr(profile.AlipayQrAsset, profile.AlipayQrSha256, AlipayQrImage, AlipayPlaceholder);
        LoadQr(profile.WeChatQrAsset, profile.WeChatQrSha256, WeChatQrImage, WeChatPlaceholder);
    }

    private static void LoadQr(
        string relativePath,
        string expectedSha256,
        System.Windows.Controls.Image image,
        System.Windows.Controls.TextBlock placeholder)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }
        var root = Path.GetFullPath(AppContext.BaseDirectory);
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            placeholder.Text = "二维码文件无效";
            return;
        }
        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                placeholder.Text = "二维码校验失败";
                return;
            }
        }
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();
        bitmap.Freeze();
        image.Source = bitmap;
        placeholder.Visibility = Visibility.Collapsed;
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}
