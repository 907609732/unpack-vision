using System.Collections.ObjectModel;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using UnpackVision.Core;

namespace UnpackVision.App;

public partial class PairedDevicesWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly ObservableCollection<PairedDeviceRow> _devices = [];

    public PairedDevicesWindow()
    {
        InitializeComponent();
        DevicesGrid.ItemsSource = _devices;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async void Refresh_OnClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        StatusText.Text = "正在连接工位主机…";
        try
        {
            await StationHostConnection.EnsureRunningAsync();
            var devices = await StationHostConnection.Http.GetFromJsonAsync<List<PairedDevice>>(
                "/api/v1/devices",
                JsonOptions) ?? [];
            _devices.Clear();
            foreach (var device in devices.OrderByDescending(item => item.LastSeenAt ?? item.PairedAt))
            {
                _devices.Add(new PairedDeviceRow(device));
            }
            StatusText.Text = devices.Count == 0
                ? "还没有已配对设备，请先生成二维码让手机扫码配对。"
                : $"共 {devices.Count} 台可用设备。";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"读取设备失败：{exception.Message}";
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

        try
        {
            using var response = await StationHostConnection.Http.DeleteAsync($"/api/v1/devices/{selected.Id}");
            response.EnsureSuccessStatusCode();
            await RefreshAsync();
        }
        catch (Exception exception)
        {
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
