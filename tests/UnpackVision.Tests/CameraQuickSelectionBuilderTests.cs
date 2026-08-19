using UnpackVision.App;
using UnpackVision.Core;
using UnpackVision.Infrastructure;

namespace UnpackVision.Tests;

public sealed class CameraQuickSelectionBuilderTests
{
    [Fact]
    public void SingleWindowsCamera_ListsEveryWindowsDeviceForQuickSwitching()
    {
        var rig = CreateRig(CameraRigMode.SingleCamera, CameraSourceType.WindowsCamera);
        var choices = CameraQuickSelectionBuilder.Build(rig,
        [
            new WindowsCameraDevice(0, "EMEET 4K", "usb#emeet"),
            new WindowsCameraDevice(1, "iVCam", "usb#ivcam")
        ]);

        Assert.Collection(
            choices,
            first =>
            {
                Assert.Equal("EMEET 4K", first.Label);
                Assert.True(first.IsLocalDevice);
                Assert.Equal(0, first.WindowsCameraIndex);
                Assert.Equal("usb#emeet", first.WindowsSymbolicLink);
            },
            second =>
            {
                Assert.Equal("iVCam", second.Label);
                Assert.True(second.IsLocalDevice);
                Assert.Equal(1, second.WindowsCameraIndex);
                Assert.Equal("usb#ivcam", second.WindowsSymbolicLink);
            });
    }

    [Fact]
    public void AutoLocal_KeepsAutomaticChoiceBeforeDiscoveredDevices()
    {
        var rig = CreateRig(CameraRigMode.SingleCamera, CameraSourceType.AutoLocal);
        var choices = CameraQuickSelectionBuilder.Build(rig,
        [
            new WindowsCameraDevice(0, "USB Camera", "usb#camera")
        ]);

        Assert.True(choices[0].IsAutomaticLocalChoice);
        Assert.Equal("自动选择本地摄像头", choices[0].Label);
        Assert.True(choices[1].IsLocalDevice);
        Assert.Equal("USB Camera", choices[1].Label);
    }

    [Fact]
    public void MultiCameraNetworkRig_KeepsProfileChoicesInsteadOfReplacingThemWithLocalDevices()
    {
        var rig = CreateRig(CameraRigMode.MultiCamera, CameraSourceType.NetworkStream);
        rig.Cameras.Add(new CameraProfile
        {
            Id = "side",
            DisplayName = "侧面 IPC",
            Enabled = true,
            SortOrder = 1,
            SourceType = CameraSourceType.NetworkStream
        });

        var choices = CameraQuickSelectionBuilder.Build(rig,
        [
            new WindowsCameraDevice(0, "USB Camera", "usb#camera")
        ]);

        Assert.Equal(["主机位 · 主机位", "侧面 IPC"], choices.Select(item => item.Label));
        Assert.DoesNotContain(choices, item => item.IsLocalDevice);
    }

    private static CameraRigOptions CreateRig(CameraRigMode mode, CameraSourceType sourceType) => new()
    {
        Mode = mode,
        Cameras =
        [
            new CameraProfile
            {
                Id = "primary",
                DisplayName = "主机位",
                Enabled = true,
                IsPrimary = true,
                SourceType = sourceType,
                SortOrder = 0
            }
        ]
    };
}
