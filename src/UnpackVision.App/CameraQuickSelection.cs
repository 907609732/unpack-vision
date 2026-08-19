using UnpackVision.Core;
using UnpackVision.Infrastructure;

namespace UnpackVision.App;

/// <summary>
/// Produces the compact picker shown at the top-left of the recording workspace.
/// A single local camera is a device choice, while network and multi-camera setups
/// remain profile choices so a quick switch never rewrites a configured IPC/NVR source.
/// </summary>
public sealed record CameraQuickSelection(
    string Label,
    string CameraId,
    int? WindowsCameraIndex = null,
    string? WindowsSymbolicLink = null,
    bool IsLocalDevice = false,
    bool IsAutomaticLocalChoice = false);

public static class CameraQuickSelectionBuilder
{
    public static bool UsesLocalDevicePicker(CameraRigOptions rig)
    {
        ArgumentNullException.ThrowIfNull(rig);
        var primary = rig.PrimaryCamera;
        return rig.Mode == CameraRigMode.SingleCamera &&
               primary?.SourceType is CameraSourceType.AutoLocal or CameraSourceType.WindowsCamera;
    }

    public static IReadOnlyList<CameraQuickSelection> Build(
        CameraRigOptions rig,
        IReadOnlyList<WindowsCameraDevice> localDevices)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(localDevices);

        var primary = rig.PrimaryCamera;
        if (primary is null)
        {
            return [];
        }

        if (!UsesLocalDevicePicker(rig))
        {
            return rig.GetSelectableCameras()
                .Select(camera => new CameraQuickSelection(
                    camera.IsPrimary ? $"{camera.DisplayName} · 主机位" : camera.DisplayName,
                    camera.Id))
                .ToArray();
        }

        var choices = new List<CameraQuickSelection>();
        if (primary.SourceType == CameraSourceType.AutoLocal)
        {
            choices.Add(new CameraQuickSelection(
                "自动选择本地摄像头",
                primary.Id,
                IsAutomaticLocalChoice: true));
        }

        choices.AddRange(localDevices.Select(device => new CameraQuickSelection(
            device.DisplayName,
            primary.Id,
            device.Index,
            device.SymbolicLink,
            IsLocalDevice: true)));

        // Media Foundation can temporarily return no devices during hot-plug. Keep the
        // saved source visible so the control does not become empty or imply data loss.
        if (choices.Count == 0)
        {
            choices.Add(new CameraQuickSelection(primary.DisplayName, primary.Id));
        }

        return choices;
    }
}
