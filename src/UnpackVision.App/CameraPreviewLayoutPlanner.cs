using UnpackVision.Core;

namespace UnpackVision.App;

internal sealed record CameraPreviewSlotPlan(
    int SlotIndex,
    int Row,
    int Column,
    int RowSpan,
    int ColumnSpan,
    CameraProfile? Camera);

/// <summary>
/// Produces deterministic monitoring-wall geometry independently from WPF controls. Keeping
/// assignment and swap rules here prevents a view-only action from mutating camera pipelines.
/// </summary>
internal static class CameraPreviewLayoutPlanner
{
    public static IReadOnlyList<CameraPreviewSlotPlan> Build(
        int requestedViewCount,
        IReadOnlyList<CameraProfile> availableCameras,
        IReadOnlyList<string>? requestedCameraIds)
    {
        var viewCount = Math.Clamp(requestedViewCount, 1, CameraRigOptions.MaximumEnabledCameras);
        var available = availableCameras
            .Where(camera => camera.Enabled)
            .GroupBy(camera => camera.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(CameraRigOptions.MaximumEnabledCameras)
            .ToArray();
        var byId = available.ToDictionary(camera => camera.Id, StringComparer.OrdinalIgnoreCase);
        var assignments = new CameraProfile?[viewCount];
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (requestedCameraIds is not null)
        {
            for (var slot = 0; slot < Math.Min(viewCount, requestedCameraIds.Count); slot++)
            {
                var id = requestedCameraIds[slot];
                if (!string.IsNullOrWhiteSpace(id) && byId.TryGetValue(id, out var camera) && used.Add(camera.Id))
                {
                    assignments[slot] = camera;
                }
            }
        }
        foreach (var camera in available.Where(camera => used.Add(camera.Id)))
        {
            var empty = Array.FindIndex(assignments, item => item is null);
            if (empty < 0)
            {
                break;
            }
            assignments[empty] = camera;
        }

        return Enumerable.Range(0, viewCount)
            .Select(slot => CreatePlan(viewCount, slot, assignments[slot]))
            .ToArray();
    }

    public static IReadOnlyList<string> Assign(
        IReadOnlyList<string> existing,
        int viewCount,
        int slotIndex,
        string cameraId)
    {
        if (slotIndex < 0 || slotIndex >= viewCount || string.IsNullOrWhiteSpace(cameraId))
        {
            throw new ArgumentOutOfRangeException(nameof(slotIndex), "预览窗口或摄像头无效");
        }
        var result = Enumerable.Range(0, viewCount)
            .Select(index => index < existing.Count ? existing[index] ?? string.Empty : string.Empty)
            .ToArray();
        var oldCameraId = result[slotIndex];
        var existingSlot = Array.FindIndex(result, id =>
            string.Equals(id, cameraId, StringComparison.OrdinalIgnoreCase));
        result[slotIndex] = cameraId;
        if (existingSlot >= 0 && existingSlot != slotIndex)
        {
            result[existingSlot] = oldCameraId;
        }
        return result;
    }

    private static CameraPreviewSlotPlan CreatePlan(int viewCount, int slot, CameraProfile? camera) =>
        viewCount switch
        {
            1 => new CameraPreviewSlotPlan(slot, 0, 0, 1, 1, camera),
            2 => new CameraPreviewSlotPlan(slot, 0, slot, 1, 1, camera),
            3 when slot == 0 => new CameraPreviewSlotPlan(slot, 0, 0, 2, 1, camera),
            3 => new CameraPreviewSlotPlan(slot, slot - 1, 1, 1, 1, camera),
            _ => new CameraPreviewSlotPlan(slot, slot / 2, slot % 2, 1, 1, camera)
        };
}
