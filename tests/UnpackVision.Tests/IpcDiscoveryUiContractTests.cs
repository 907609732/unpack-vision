using System.Xml.Linq;
using UnpackVision.App;
using UnpackVision.Core;

namespace UnpackVision.Tests;

public sealed class IpcDiscoveryUiContractTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void NetworkStreamEditor_ExposesDiscoverProgressResultsAndPreviewControls()
    {
        var dialog = Load("CameraProfileDialog.xaml");
        var namedElements = dialog.Descendants()
            .Where(element => element.Attribute(Xaml + "Name") is not null)
            .ToDictionary(
                element => (string)element.Attribute(Xaml + "Name")!,
                StringComparer.Ordinal);

        Assert.Equal(
            "DiscoverIpc_OnClick",
            (string?)namedElements["DiscoverIpcButton"].Attribute("Click"));
        Assert.Equal(
            "Collapsed",
            (string?)namedElements["IpcDiscoveryProgress"].Attribute("Visibility"));
        Assert.Equal(
            "Collapsed",
            (string?)namedElements["IpcDiscoveryResultList"].Attribute("Visibility"));
        Assert.Equal(
            "StartCameraPreview_OnClick",
            (string?)namedElements["StartCameraPreviewButton"].Attribute("Click"));
        Assert.Equal(
            "StopCameraPreview_OnClick",
            (string?)namedElements["StopCameraPreviewButton"].Attribute("Click"));

        Assert.Equal(Presentation + "TextBlock", namedElements["IpcDiscoveryStatusText"].Name);
        Assert.Equal(Presentation + "TextBlock", namedElements["IpcDiscoverySourceText"].Name);
        Assert.Equal(Presentation + "TextBlock", namedElements["IpcDiscoveryProgressText"].Name);
        Assert.Equal(Presentation + "ProgressBar", namedElements["IpcDiscoveryProgress"].Name);
        Assert.Equal(Presentation + "ItemsControl", namedElements["IpcDiscoveryNoticeList"].Name);
        Assert.Equal(Presentation + "ListBox", namedElements["IpcDiscoveryResultList"].Name);
        Assert.Equal(Presentation + "Image", namedElements["CameraPreviewImage"].Name);

        Assert.Equal(
            "Collapsed",
            (string?)namedElements["IpcDiscoveryProgressText"].Attribute("Visibility"));
        Assert.Equal(
            "Collapsed",
            (string?)namedElements["IpcDiscoveryNoticeList"].Attribute("Visibility"));
        Assert.Contains(
            "发现成功不等于视频流已验证",
            (string?)namedElements["IpcDiscoveryVerificationNotice"].Attribute("Text"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void DiscoveryStaysInNetworkCard_WhilePreviewIsSharedByEverySource()
    {
        var dialog = Load("CameraProfileDialog.xaml");
        var networkCard = dialog.Descendants(Presentation + "Border")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "NetworkStreamCard");
        var networkNames = networkCard.Descendants()
            .Select(element => (string?)element.Attribute(Xaml + "Name"))
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(
            new[]
            {
                "DiscoverIpcButton",
                "IpcDiscoverySourceText",
                "IpcDiscoveryStatusText",
                "IpcDiscoveryProgress",
                "IpcDiscoveryProgressText",
                "IpcDiscoveryNoticeList",
                "IpcDiscoveryVerificationNotice",
                "IpcDiscoveryResultList"
            },
            name => Assert.Contains(name, networkNames));
        Assert.DoesNotContain("CameraPreviewImage", networkNames);
        Assert.Equal("Collapsed", (string?)networkCard.Attribute("Visibility"));

        var previewCard = dialog.Descendants(Presentation + "Border")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "CameraPreviewCard");
        var previewNames = previewCard.Descendants()
            .Select(element => (string?)element.Attribute(Xaml + "Name"))
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(
            new[]
            {
                "CameraPreviewImage",
                "StartCameraPreviewButton",
                "StopCameraPreviewButton"
            },
            name => Assert.Contains(name, previewNames));

        var cameraSelector = dialog.Descendants(Presentation + "ComboBox")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "CameraIndexInput");
        Assert.Equal(
            "CameraIndexInput_OnSelectionChanged",
            (string?)cameraSelector.Attribute("SelectionChanged"));
    }

    [Fact]
    public void DiscoveryCodeKeepsPartialResultsAndNeverTreatsOnvifServiceAddressAsRtsp()
    {
        var code = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "CameraProfileDialog.xaml.cs"));

        Assert.Contains("scanResult.Devices", code, StringComparison.Ordinal);
        Assert.Contains("scanResult.Notices", code, StringComparison.Ordinal);
        Assert.Contains("IpcDiscoveryPresentation.PrepareNotices", code, StringComparison.Ordinal);
        Assert.Contains("IpcDiscoveryPresentation.GetSourceSummary", code, StringComparison.Ordinal);

        var selectionStart = code.IndexOf(
            "private async void IpcDiscoveryResultList_OnSelectionChanged",
            StringComparison.Ordinal);
        var selectionEnd = code.IndexOf(
            "private async void DiscoverHikvisionChannels_OnClick",
            selectionStart,
            StringComparison.Ordinal);
        Assert.True(selectionStart >= 0 && selectionEnd > selectionStart);
        var selectionHandler = code[selectionStart..selectionEnd];
        Assert.Contains("TryGetHikvisionRtspHost", selectionHandler, StringComparison.Ordinal);
        Assert.Contains("BuildHikvisionRtspUrl", selectionHandler, StringComparison.Ordinal);
        Assert.Contains("ONVIF device_service", selectionHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("device.Addresses", selectionHandler, StringComparison.Ordinal);
    }

    [Fact]
    public void DiscoveryPresentationLabelsOnvifAndExplainsSadpPortContention()
    {
        var device = Device("HIKVISION");

        Assert.Equal("ONVIF 标准", IpcDiscoveryPresentation.GetSourceLabel(device.Source));
        Assert.Equal("兼容探测", IpcDiscoveryPresentation.GetSourceLabel(IpcDiscoverySource.CompatibilityProbe));
        Assert.Equal("海康 SADP", IpcDiscoveryPresentation.GetSourceLabel(IpcDiscoverySource.HikvisionSadp));
        Assert.Equal("局域网发现", IpcDiscoveryPresentation.GetSourceLabel((IpcDiscoverySource)999));
        Assert.Equal("ONVIF 标准", IpcDiscoveryPresentation.GetSourceSummary([device]));

        var notices = IpcDiscoveryPresentation.PrepareNotices(
        [
            "SADP UDP 37020 端口被海康交付助手占用",
            "部分网卡没有可用地址"
        ]);

        Assert.Equal(2, notices.Count);
        Assert.Contains("ONVIF 标准和兼容探测结果仍会保留", notices[0], StringComparison.Ordinal);
        Assert.Contains("关闭交付助手后可重新扫描", notices[0], StringComparison.Ordinal);

        var sadpDevice = device with { Source = IpcDiscoverySource.HikvisionSadp };
        var noticesWithSuccessfulSadp = IpcDiscoveryPresentation.PrepareNotices(
            ["SADP UDP 37020 端口被海康交付助手占用"],
            [sadpDevice]);
        Assert.Empty(noticesWithSuccessfulSadp);
    }

    [Fact]
    public void MergedDiscoveryCandidateShowsBothSourcesWithoutChangingLegacySource()
    {
        var merged = Device("HIKVISION") with
        {
            Source = IpcDiscoverySource.HikvisionSadp,
            Sources =
            [
                IpcDiscoverySource.OnvifWsDiscovery,
                IpcDiscoverySource.HikvisionSadp
            ]
        };

        Assert.Equal(IpcDiscoverySource.HikvisionSadp, merged.Source);
        Assert.Equal("ONVIF 标准、海康 SADP", IpcDiscoveryPresentation.GetSourceSummary([merged]));
    }

    [Fact]
    public void NoticesUseStableOrdinalDeduplicationAfterDisplayNormalization()
    {
        const string compatibilityNotice = "标准 ONVIF 探测未返回设备，已自动使用兼容探测。";

        var notices = IpcDiscoveryPresentation.PrepareNotices(
        [
            compatibilityNotice,
            compatibilityNotice,
            $"  {compatibilityNotice}\u200B  ",
            "部分网卡没有可用地址",
            compatibilityNotice
        ]);

        Assert.Equal(
            [compatibilityNotice, "部分网卡没有可用地址"],
            notices);
    }

    [Fact]
    public void DialogExplainsScanDurationAndAwaitsActiveWorkBeforeClosing()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "CameraProfileDialog.xaml"));
        var code = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "CameraProfileDialog.xaml.cs"));

        Assert.Contains("约需 5–10 秒", xaml, StringComparison.Ordinal);
        Assert.Contains("Closing += CameraProfileDialog_OnClosing", code, StringComparison.Ordinal);
        Assert.Contains("await AwaitCancellationAsync(_ipcDiscoveryTask)", code, StringComparison.Ordinal);
        Assert.Contains("await AwaitCancellationAsync(_hikvisionDiscoveryTask)", code, StringComparison.Ordinal);
        Assert.Contains("if (!HasActiveBackgroundOperation())", code, StringComparison.Ordinal);
        Assert.Contains("QueueDeferredClose();", code, StringComparison.Ordinal);
        Assert.Contains("Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle", code, StringComparison.Ordinal);
        var closingHandler = code[code.IndexOf("private async void CameraProfileDialog_OnClosing", StringComparison.Ordinal)..
            code.IndexOf("private bool HasActiveBackgroundOperation", StringComparison.Ordinal)];
        Assert.DoesNotContain("\n        Close();", closingHandler.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("Closed += async", code, StringComparison.Ordinal);
    }

    [Fact]
    public void HikvisionCandidateUsesRemoteIpInsteadOfOnvifDeviceServiceAddress()
    {
        var device = Device("HIKVISION");

        Assert.True(IpcDiscoveryPresentation.TryGetHikvisionRtspHost(device, out var host));
        Assert.Equal("192.168.50.20", host);
        Assert.DoesNotContain("device_service", host, StringComparison.OrdinalIgnoreCase);

        var sadpCandidate = Device("Unknown") with { Source = IpcDiscoverySource.HikvisionSadp };
        Assert.True(IpcDiscoveryPresentation.TryGetHikvisionRtspHost(sadpCandidate, out _));

        var generic = Device("Generic Camera") with { Manufacturer = "Other" };
        Assert.False(IpcDiscoveryPresentation.TryGetHikvisionRtspHost(generic, out _));
    }

    [Fact]
    public void SelectingAnUnverifiedDiscoveryCandidateNeverAutomaticallySendsSavedCredentials()
    {
        var code = LoadDialogCode();
        var selectionHandler = Slice(
            code,
            "private async void IpcDiscoveryResultList_OnSelectionChanged",
            "private void ClearDiscoveredCandidateCredentials");

        Assert.Contains("ClearDiscoveredCandidateCredentials();", selectionHandler, StringComparison.Ordinal);
        Assert.Contains("StopCameraPreviewAsync(updateStatus: false)", selectionHandler, StringComparison.Ordinal);
        Assert.Contains("点击“开始预览”", selectionHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("RestartCameraPreviewAsync", selectionHandler, StringComparison.Ordinal);

        var clearCredentials = Slice(
            code,
            "private void ClearDiscoveredCandidateCredentials",
            "private async void DiscoverHikvisionChannels_OnClick");
        Assert.Contains("NetworkUsernameInput.Clear();", clearCredentials, StringComparison.Ordinal);
        Assert.Contains("NetworkPasswordInput.Clear();", clearCredentials, StringComparison.Ordinal);
        Assert.Contains("HikvisionUsernameInput.Clear();", clearCredentials, StringComparison.Ordinal);
        Assert.Contains("HikvisionPasswordInput.Clear();", clearCredentials, StringComparison.Ordinal);
    }

    private static IpcDiscoveryDevice Device(string manufacturer) => new(
        "urn:uuid:test-camera",
        "测试 IPC",
        manufacturer,
        "Model-Test",
        "192.168.50.20",
        ["http://192.168.50.20/onvif/device_service"],
        []);

    private static string LoadDialogCode() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "CameraProfileDialog.xaml.cs"));

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing source marker: {endMarker}");
        return source[start..end];
    }

    private static XDocument Load(string fileName) =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestData", fileName));
}
