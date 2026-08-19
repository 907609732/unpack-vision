using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UnpackVision.Infrastructure;
using UnpackVision.Infrastructure.Diagnostics;

namespace UnpackVision.App;

public partial class DevicePairingWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource _lifetime = new();
    private string[] _lanCandidates = [];
    private DateTimeOffset _expiresAt;
    private bool _refreshing;
    private bool _closed;

    public DevicePairingWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await OnLoadedAsync();
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            _timer.Stop();
        };
        _timer.Tick += async (_, _) => await OnTimerTickAsync();
    }

    private async Task OnLoadedAsync()
    {
        using var operation = App.UiWatchdog?.BeginOperation("pairing.load");
        StatusText.Text = "正在检测局域网…";
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            _lanCandidates = await Task
                .Run(GetLanIpv4Candidates, _lifetime.Token)
                .WaitAsync(TimeSpan.FromSeconds(5), _lifetime.Token);
            LanAddressBox.ItemsSource = _lanCandidates;
            LanAddressBox.SelectedItem = _lanCandidates.FirstOrDefault();
            DiagnosticLog.Information(
                "局域网探测完成，可用地址数量 {CandidateCount}，耗时 {ElapsedMilliseconds} 毫秒",
                _lanCandidates.Length,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);

            if (_lanCandidates.Length == 0)
            {
                throw new InvalidOperationException("未找到处于 Windows“专用网络”的局域网 IPv4 地址");
            }

            await RefreshPairingAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Closing the dialog intentionally cancels background discovery and HTTP work.
        }
        catch (TimeoutException exception)
        {
            DiagnosticLog.Warning(exception, "局域网探测超时");
            StatusText.Text = "局域网检测超时，请检查 Windows 网络配置后重试。";
            ExpiryText.Text = "打开日志目录可查看本次超时的详细时间。";
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error(exception, "初始化手机配对窗口失败");
            StatusText.Text = $"无法初始化手机配对：{exception.Message}";
            ExpiryText.Text = "请确认当前网络为专用网络。";
        }
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
        RefreshButton.IsEnabled = false;
        _timer.Stop();
        PairingQrImage.Source = null;
        StatusText.Text = "正在连接工位主机…";
        var stage = "启动工位主机";
        var startedAt = Stopwatch.GetTimestamp();
        using var operation = App.UiWatchdog?.BeginOperation("pairing.refresh");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            DiagnosticLog.Information("开始生成手机配对二维码");
            await StationHostConnection.EnsureRunningAsync(timeout.Token);
            stage = "同步多 IP 监听";
            var boundAddresses = await StationHostConnection.SynchronizeLanBindingsAsync(
                _lanCandidates,
                timeout.Token);
            var previouslySelected = LanAddressBox.SelectedItem as string;
            _lanCandidates = boundAddresses
                .OrderBy(address => AddressPriority(IPAddress.Parse(address)))
                .ToArray();
            LanAddressBox.ItemsSource = _lanCandidates;
            LanAddressBox.SelectedItem = previouslySelected is not null &&
                                         _lanCandidates.Contains(previouslySelected, StringComparer.Ordinal)
                ? previouslySelected
                : _lanCandidates.FirstOrDefault();
            var selectedAddress = GetSelectedLanIpv4();
            stage = "创建配对会话";
            using var response = await StationHostConnection.Http.PostAsync(
                $"/api/v1/pairing/sessions?address={Uri.EscapeDataString(selectedAddress.ToString())}",
                null,
                timeout.Token);
            response.EnsureSuccessStatusCode();
            stage = "解析配对响应";
            using var descriptor = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(timeout.Token));
            var root = descriptor.RootElement;
            var lanAddress = root.GetProperty("stationAddress").GetString()
                ?? throw new InvalidOperationException("工位主机没有返回安全配对地址");
            if (!Uri.TryCreate(lanAddress, UriKind.Absolute, out var safeAddress) ||
                !string.Equals(safeAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("工位主机返回了不安全的配对地址");
            }
            stage = "生成二维码";
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
            DiagnosticLog.Information(
                "手机配对二维码生成成功，耗时 {ElapsedMilliseconds} 毫秒",
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Closing the dialog intentionally cancels background discovery and HTTP work.
        }
        catch (OperationCanceledException exception)
        {
            DiagnosticLog.Warning(
                exception,
                "生成配对二维码超时，阶段 {PairingStage}，耗时 {ElapsedMilliseconds} 毫秒",
                stage,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            StatusText.Text = $"生成配对二维码超时（{stage}），请重试。";
            ExpiryText.Text = "本次操作已自动停止，不会继续卡住界面。";
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error(
                exception,
                "生成配对二维码失败，阶段 {PairingStage}，耗时 {ElapsedMilliseconds} 毫秒",
                stage,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            StatusText.Text = $"无法生成配对二维码：{exception.Message}";
            ExpiryText.Text = "请确认当前网络为专用网络，并检查 5273 端口是否被占用。";
        }
        finally
        {
            _refreshing = false;
            RefreshButton.IsEnabled = !_closed;
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

    private IPAddress GetSelectedLanIpv4()
    {
        var value = LanAddressBox.Text.Trim();
        if (!IPAddress.TryParse(value, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork ||
            !IsPrivate(address))
        {
            throw new InvalidOperationException("请选择有效的本机局域网 IPv4 地址");
        }

        if (!_lanCandidates.Contains(value, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("所选地址不属于当前电脑，请重新选择局域网地址");
        }
        return address;
    }

    private static string[] GetLanIpv4Candidates()
    {
        var privateIndexes = GetPrivateNetworkInterfaceIndexes();
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == OperationalStatus.Up)
            .Where(item => item.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .Where(item =>
            {
                try
                {
                    return privateIndexes.Contains(
                        item.GetIPProperties().GetIPv4Properties()?.Index ?? -1);
                }
                catch (NetworkInformationException)
                {
                    return false;
                }
            })
            .SelectMany(item => item.GetIPProperties().UnicastAddresses)
            .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork &&
                           item.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred &&
                           IsPrivate(item.Address))
            .OrderBy(item => AddressPriority(item.Address))
            .Select(item => item.Address.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static HashSet<int> GetPrivateNetworkInterfaceIndexes()
    {
        try
        {
            var powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe");
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = powershell,
                Arguments =
                    "-NoProfile -NonInteractive -Command " +
                    "\"Get-NetConnectionProfile | Where-Object NetworkCategory -eq 'Private' | " +
                    "Select-Object -ExpandProperty InterfaceIndex\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process is null || !process.WaitForExit(3000))
            {
                try { process?.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                DiagnosticLog.Warning("Windows 专用网络探测进程在 3000 毫秒内未完成");
                return [];
            }
            return process.StandardOutput.ReadToEnd()
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => int.TryParse(value, out var index) ? index : -1)
                .Where(index => index >= 0)
                .ToHashSet();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            DiagnosticLog.Warning(exception, "无法读取 Windows 专用网络配置");
            return [];
        }
    }

    private static int AddressPriority(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes[0] == 192 && bytes[1] == 168)
        {
            return 0;
        }
        return bytes[0] == 10 ? 1 : 2;
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
