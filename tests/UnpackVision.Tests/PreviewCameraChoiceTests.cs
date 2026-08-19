using UnpackVision.App;
using UnpackVision.Core;
using UnpackVision.Infrastructure;

namespace UnpackVision.Tests;

public sealed class PreviewCameraChoiceTests
{
    [Fact]
    public void BuilderIncludesActiveInactiveAndUnconfiguredWindowsSources()
    {
        var rig = CreateFourCameraRig();
        rig.Cameras.Add(new CameraProfile
        {
            Id = "spare-ipc",
            DisplayName = "备用 IPC",
            Enabled = false,
            SourceType = CameraSourceType.NetworkStream,
            SortOrder = 4
        });

        var choices = PreviewCameraChoiceBuilder.Build(rig,
        [
            new WindowsCameraDevice(0, "4K USB", "usb#configured"),
            new WindowsCameraDevice(1, "iVCam", "usb#ivcam")
        ]);

        Assert.Equal(4, choices.Count(choice => choice.Group == PreviewCameraChoiceGroup.Active));
        Assert.Contains(choices, choice =>
            choice.Group == PreviewCameraChoiceGroup.Configured && choice.CameraId == "spare-ipc");
        Assert.Contains(choices, choice =>
            choice.Group == PreviewCameraChoiceGroup.LocalDevice &&
            choice.Label == "iVCam" &&
            choice.WindowsSymbolicLink == "usb#ivcam");
        Assert.All(
            choices.Where(choice => choice.Group != PreviewCameraChoiceGroup.Active),
            choice => Assert.Equal(PreviewCameraChoiceAction.ConfigureRig, choice.Action));
        Assert.DoesNotContain(choices, choice =>
            choice.Group == PreviewCameraChoiceGroup.LocalDevice && choice.Label == "4K USB");
    }

    [Fact]
    public void MainWindowPreviewAssignmentChangesLayoutWithoutRebuildingOrMutatingRig()
    {
        var code = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "MainWindow.xaml.cs"));
        var start = code.IndexOf(
            "private async void AssignPreviewCamera_OnClick",
            StringComparison.Ordinal);
        var end = code.IndexOf(
            "private async void OpenCameraSettingsFromPreview_OnClick",
            start,
            StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start);
        var handler = code[start..end];
        Assert.Contains("CameraPreviewLayoutPlanner.Assign", handler, StringComparison.Ordinal);
        Assert.Contains("_settings.CameraRig.EnabledCameras", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("RecreateCameraBackendAsync", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("PreviewCameraActivator", handler, StringComparison.Ordinal);
        Assert.DoesNotContain(".Enabled =", handler, StringComparison.Ordinal);
        Assert.DoesNotContain(".IsPrimary =", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("_settings.CameraRig =", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void InactiveConfiguredProfileIsAConfigurationEntryPointOnly()
    {
        var rig = CreateFullCameraRig();
        rig.Cameras.Add(new CameraProfile
        {
            Id = "spare-ipc",
            DisplayName = "备用 IPC",
            Enabled = false,
            SourceType = CameraSourceType.NetworkStream,
            SortOrder = 4
        });

        var choice = PreviewCameraChoiceBuilder.Build(rig, [])
            .Single(item => item.CameraId == "spare-ipc");

        Assert.Equal(PreviewCameraChoiceAction.ConfigureRig, choice.Action);
        Assert.True(rig.Cameras.Single(camera => camera.Id == "side").Enabled);
        Assert.False(rig.Cameras.Single(camera => camera.Id == "spare-ipc").Enabled);
        Assert.Equal(CameraRigOptions.MaximumEnabledCameras, rig.EnabledCameras.Count);
        Assert.Equal("primary", rig.PrimaryCamera?.Id);
    }

    [Fact]
    public void UnconfiguredWindowsDeviceCannotTransferPrimaryRole()
    {
        var rig = CreateFullCameraRig();
        var choices = PreviewCameraChoiceBuilder.Build(rig,
        [
            new WindowsCameraDevice(5, "iVCam", "usb#ivcam")
        ]);

        var choice = choices.Single(item => item.WindowsSymbolicLink == "usb#ivcam");

        Assert.Equal(PreviewCameraChoiceAction.ConfigureRig, choice.Action);
        Assert.DoesNotContain(rig.Cameras, camera => camera.WindowsSymbolicLink == "usb#ivcam");
        Assert.True(rig.Cameras.Single(camera => camera.Id == "primary").IsPrimary);
        Assert.Equal(CameraRigOptions.MaximumEnabledCameras, rig.EnabledCameras.Count);
    }

    [Fact]
    public void ActiveCameraIsTheOnlyDirectPreviewAssignment()
    {
        var rig = CreateFourCameraRig();
        var choices = PreviewCameraChoiceBuilder.Build(rig, []);

        var choice = choices.Single(item => item.CameraId == "side");

        Assert.Equal(PreviewCameraChoiceAction.AssignPreview, choice.Action);
        Assert.Equal(4, rig.EnabledCameras.Count);
        Assert.Equal("primary", rig.PrimaryCamera?.Id);
    }

    private static CameraRigOptions CreateFourCameraRig() => new()
    {
        Mode = CameraRigMode.MultiCamera,
        Cameras =
        [
            new CameraProfile
            {
                Id = "primary",
                DisplayName = "主机位",
                Enabled = true,
                IsPrimary = true,
                SortOrder = 0,
                SourceType = CameraSourceType.WindowsCamera,
                WindowsSymbolicLink = "usb#configured"
            },
            new CameraProfile { Id = "side", DisplayName = "侧面", Enabled = true, SortOrder = 1 },
            new CameraProfile { Id = "back", DisplayName = "后方", Enabled = true, SortOrder = 2 },
            new CameraProfile { Id = "nvr", DisplayName = "录像机", Enabled = true, SortOrder = 3 }
        ]
    };

    private static CameraRigOptions CreateFullCameraRig()
    {
        var rig = CreateFourCameraRig();
        for (var index = rig.Cameras.Count; index < CameraRigOptions.MaximumEnabledCameras; index++)
        {
            rig.Cameras.Add(new CameraProfile
            {
                Id = $"extra-{index}",
                DisplayName = $"机位 {index + 1}",
                Enabled = true,
                SortOrder = index
            });
        }
        return rig;
    }
}
