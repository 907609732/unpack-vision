using UnpackVision.Core;
using UnpackVision.Infrastructure;

namespace UnpackVision.App;

internal enum PreviewCameraChoiceGroup
{
    Active,
    Configured,
    LocalDevice
}

internal enum PreviewCameraChoiceAction
{
    AssignPreview,
    ConfigureRig
}

internal sealed record PreviewCameraChoice(
    string Label,
    PreviewCameraChoiceGroup Group,
    string? CameraId = null,
    int? WindowsCameraIndex = null,
    string? WindowsSymbolicLink = null)
{
    public PreviewCameraChoiceAction Action =>
        Group == PreviewCameraChoiceGroup.Active && !string.IsNullOrWhiteSpace(CameraId)
            ? PreviewCameraChoiceAction.AssignPreview
            : PreviewCameraChoiceAction.ConfigureRig;
}

/// <summary>
/// Builds the camera list shown by a monitoring-wall tile. Active pipelines are assignable
/// preview choices. Saved inactive sources and newly discovered Windows devices remain visible,
/// but are configuration entry points rather than implicit recording-rig mutations.
/// </summary>
internal static class PreviewCameraChoiceBuilder
{
    public static IReadOnlyList<PreviewCameraChoice> Build(
        CameraRigOptions rig,
        IReadOnlyList<WindowsCameraDevice> localDevices)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(localDevices);

        var activeIds = rig.EnabledCameras
            .Select(camera => camera.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var choices = new List<PreviewCameraChoice>();
        foreach (var camera in rig.Cameras.OrderBy(camera => camera.SortOrder))
        {
            var localDevice = FindLocalDevice(camera, localDevices);
            var label = Describe(camera, localDevice);
            choices.Add(new PreviewCameraChoice(
                label,
                activeIds.Contains(camera.Id)
                    ? PreviewCameraChoiceGroup.Active
                    : PreviewCameraChoiceGroup.Configured,
                camera.Id));
        }

        foreach (var device in localDevices.Where(device => !rig.Cameras.Any(camera => Matches(camera, device))))
        {
            choices.Add(new PreviewCameraChoice(
                device.DisplayName,
                PreviewCameraChoiceGroup.LocalDevice,
                WindowsCameraIndex: device.Index,
                WindowsSymbolicLink: device.SymbolicLink));
        }
        return choices;
    }

    private static WindowsCameraDevice? FindLocalDevice(
        CameraProfile camera,
        IReadOnlyList<WindowsCameraDevice> devices) =>
        devices.FirstOrDefault(device => Matches(camera, device));

    private static bool Matches(CameraProfile camera, WindowsCameraDevice device) =>
        camera.SourceType == CameraSourceType.WindowsCamera &&
        ((!string.IsNullOrWhiteSpace(camera.WindowsSymbolicLink) &&
          string.Equals(camera.WindowsSymbolicLink, device.SymbolicLink, StringComparison.OrdinalIgnoreCase)) ||
         (string.IsNullOrWhiteSpace(camera.WindowsSymbolicLink) &&
          camera.LegacyCameraIndex == device.Index));

    private static string Describe(CameraProfile camera, WindowsCameraDevice? localDevice)
    {
        var source = camera.SourceType switch
        {
            CameraSourceType.AutoLocal => "自动本地摄像头",
            CameraSourceType.WindowsCamera => localDevice?.DisplayName ?? "Windows 摄像头",
            CameraSourceType.NetworkStream => "IPC / RTSP",
            CameraSourceType.HikvisionRecorder => $"海康录像机 · 通道 {camera.HikvisionChannel}",
            _ => "摄像头"
        };
        var primary = camera.IsPrimary ? " · 主机位" : string.Empty;
        return string.Equals(camera.DisplayName, source, StringComparison.OrdinalIgnoreCase)
            ? $"{camera.DisplayName}{primary}"
            : $"{camera.DisplayName}{primary} — {source}";
    }
}
