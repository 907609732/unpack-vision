using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UnpackVision.Infrastructure;

namespace UnpackVision.App;

public partial class DevicePairingWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset _expiresAt;
    private bool _refreshing;
    private bool _closed;

    public DevicePairingWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            PopulateLanAddressChoices();
            await RefreshPairingAsync();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
        };
        _timer.Tick += async (_, _) => await OnTimerTickAsync();
    }

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e) =>
        await RefreshPairingAsync();

    private async Task RefreshPairingAsync()
    {
        if (_refreshing || _closed)
        {
            return;
        }
        _refreshing = true;
        _timer.Stop();
        PairingQrImage.Source = null;
        StatusText.Text = "正在连接工位主机…";
        try
        {
            await StationHostConnection.EnsureRunningAsync();
            using var response = await StationHostConnection.Http.PostAsync("/api/v1/pairing/sessions", null);
            response.EnsureSuccessStatusCode();
            using var descriptor = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = descriptor.RootElement;
            var lanAddress = $"http://{GetSelectedLanIpv4()}:5271";
            var payload = JsonSerializer.Serialize(new
            {
                id = root.GetProperty("id").GetGuid(),
                stationId = root.GetProperty("stationId").GetString(),
                stationAddress = lanAddress,
                certificateFingerprint = root.GetProperty("certificateFingerprint").GetString(),
                token = root.GetProperty("token").GetString(),
                expiresAt = root.GetProperty("expiresAt").GetDateTimeOffset()
            });
            _expiresAt = root.GetProperty("expiresAt").GetDateTimeOffset();
            PairingQrImage.Source = ToBitmapImage(BarcodePresentationService.CreateQrCodePng(payload, 520));
            AddressText.Text = $"工位地址：{lanAddress}";
            StatusText.Text = "工位主机已就绪，请用手机扫描二维码";
            _timer.Start();
            UpdateExpiryText();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"无法生成配对二维码：{exception.Message}";
            ExpiryText.Text = "请确认当前网络为专用网络，并检查 5271 端口是否被占用。";
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task OnTimerTickAsync()
    {
        if (_expiresAt > DateTimeOffset.Now)
        {
            UpdateExpiryText();
            return;
        }
        _timer.Stop();
        ExpiryText.Text = "二维码已过期，正在自动刷新…";
        await RefreshPairingAsync();
    }

    private void PopulateLanAddressChoices()
    {
        var candidates = GetLanIpv4Candidates();
        LanAddressBox.ItemsSource = candidates;
        LanAddressBox.SelectedItem = candidates.FirstOrDefault();
    }

    private IPAddress GetSelectedLanIpv4()
    {
        var value = LanAddressBox.Text.Trim();
        if (!IPAddress.TryParse(value, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork ||
            !IsPrivate(address))
        {
            throw new InvalidOperationException("请选择有效的本机局域网 IPv4 地址");
        }

        if (!GetLanIpv4Candidates().Contains(value, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("所选地址不属于当前电脑，请重新选择局域网地址");
        }
        return address;
    }

    private static string[] GetLanIpv4Candidates()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == OperationalStatus.Up)
            .Where(item => item.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .SelectMany(item => item.GetIPProperties().UnicastAddresses)
            .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork &&
                           item.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred &&
                           IsPrivate(item.Address))
            .OrderBy(item => AddressPriority(item.Address))
            .Select(item => item.Address.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static int AddressPriority(IPAddress address)
    {
        var text = address.ToString();
        if (text.StartsWith("192.168.31.", StringComparison.Ordinal))
        {
            return 0;
        }
        var bytes = address.GetAddressBytes();
        if (bytes[0] == 192 && bytes[1] == 168)
        {
            return 1;
        }
        return bytes[0] == 10 ? 2 : 3;
    }

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               bytes[0] == 192 && bytes[1] == 168 ||
               bytes[0] == 172 && bytes[1] is >= 16 and <= 31;
    }

    private static BitmapImage ToBitmapImage(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void UpdateExpiryText()
    {
        var remaining = _expiresAt - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
        {
            _timer.Stop();
            ExpiryText.Text = "二维码已过期，请点击刷新";
            return;
        }
        ExpiryText.Text = $"有效期剩余：{remaining.Minutes:00}:{remaining.Seconds:00}";
    }
}
