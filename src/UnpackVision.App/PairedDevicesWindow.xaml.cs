using System.Collections.ObjectModel;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using UnpackVision.Core;
using UnpackVision.Infrastructure.Diagnostics;

namespace UnpackVision.App;

public partial class PairedDevicesWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly ObservableCollection<PairedDeviceRow> _devices = [];
    private readonly CancellationTokenSource _lifetime = new();
    private bool _refreshing;

    public PairedDevicesWindow()
    {
        InitializeComponent();
        DevicesGrid.ItemsSource = _devices;
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => _lifetime.Cancel();
    }

    private async void Refresh_OnClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        RefreshButton.IsEnabled = false;
        StatusText.Text = "正在连接工位主机…";
        using var operation = App.UiWatchdog?.BeginOperation("paired-devices.refresh");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            DiagnosticLog.Information("开始读取已配对设备列表");
            await StationHostConnection.EnsureRunningAsync(timeout.Token);
            var devices = await StationHostConnection.Http.GetFromJsonAsync<List<PairedDevice>>(
                "/api/v1/devices",
                JsonOptions,
                timeout.Token) ?? [];
            _devices.Clear();
            foreach (var device in devices.OrderByDescending(item => item.LastSeenAt ?? item.PairedAt))
            {
                _devices.Add(new PairedDeviceRow(device));
            }
            DiagnosticLog.Information("已配对设备列表读取成功，设备数量 {DeviceCount}", devices.Count);
            StatusText.Text = devices.Count == 0
                ? "还没有已配对设备，请先生成二维码让手机扫码配对。"
                : $"共 {devices.Count} 台可用设备。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Closing the window intentionally cancels host startup and list retrieval.
        }
        catch (OperationCanceledException exception)
        {
            DiagnosticLog.Warning(exception, "读取已配对设备列表超时");
            StatusText.Text = "读取设备超时，本次操作已自动停止，请重试。";
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error(exception, "读取已配对设备列表失败");
            StatusText.Text = $"读取设备失败：{exception.Message}";
        }
        finally
        {
            _refreshing = false;
            RefreshButton.IsEnabled = !_lifetime.IsCancellationRequested;
        }
    }

    private async void Revoke_OnClick(object sender, RoutedEventArgs e)
    {
        if (DevicesGrid.SelectedItem is not PairedDeviceRow selected)
        {
            MessageBox.Show(this, "请先选择一台设备。", "已配对设备", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(
                this,
                $"确定永久删除“{selected.Name}”吗？删除后该手机会立即断开，必须重新扫码配对才能使用。",
                "删除设备",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        using var operation = App.UiWatchdog?.BeginOperation("paired-devices.revoke");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            DiagnosticLog.Information("开始撤销一个已配对设备");
            using var response = await StationHostConnection.Http.DeleteAsync(
                $"/api/v1/devices/{selected.Id}",
                timeout.Token);
            response.EnsureSuccessStatusCode();
            DiagnosticLog.Information("已配对设备撤销成功");
            await RefreshAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Closing the window intentionally cancels the revocation request.
        }
        catch (OperationCanceledException exception)
        {
            DiagnosticLog.Warning(exception, "撤销已配对设备超时");
            MessageBox.Show(this, "删除超时，本次操作已自动停止，请重试。", "已配对设备", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error(exception, "撤销已配对设备失败");
            MessageBox.Show(this, $"删除失败：{exception.Message}", "已配对设备", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private sealed class PairedDeviceRow
    {
        public PairedDeviceRow(PairedDevice device)
        {
            Id = device.Id;
            Name = string.IsNullOrWhiteSpace(device.Name) ? "未命名手机" : device.Name;
            IsRevoked = device.IsRevoked;
            Status = device.IsRevoked ? "已撤销" : "可用";
            Roles = string.Join("、", device.Roles);
            Scopes = string.Join("、", device.Scopes);
            PairedAt = device.PairedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            LastSeenAt = device.LastSeenAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "从未连接";
        }

        public string Id { get; }
        public string Name { get; }
        public bool IsRevoked { get; }
        public string Status { get; }
        public string Roles { get; }
        public string Scopes { get; }
        public string PairedAt { get; }
        public string LastSeenAt { get; }
    }
}
