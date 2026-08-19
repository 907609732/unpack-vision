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
    /// <summary>
    /// Single-camera mode always renders one full preview without overwriting the user's saved
    /// monitoring-wall layout. Returning to multi-camera mode therefore restores the prior view.
    /// </summary>
    public static int ResolveViewCount(
        CameraRigMode mode,
        int requestedViewCount,
        int maximumViewCount = CameraRigOptions.MaximumEnabledCameras) =>
        mode == CameraRigMode.SingleCamera
            ? 1
            : Math.Clamp(requestedViewCount, 1, Math.Max(1, maximumViewCount));

    public static IReadOnlyList<CameraPreviewSlotPlan> Build(
        int requestedViewCount,
        IReadOnlyList<CameraProfile> availableCameras,
        IReadOnlyList<string>? requestedCameraIds,
        int maximumViewCount = CameraRigOptions.MaximumEnabledCameras)
    {
        var effectiveMaximum = Math.Max(1, maximumViewCount);
        var viewCount = Math.Clamp(requestedViewCount, 1, effectiveMaximum);
        var available = availableCameras
            .Where(camera => camera.Enabled)
            .GroupBy(camera => camera.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(effectiveMaximum)
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

        var (_, columns) = DetermineGridDimensions(viewCount);
        return Enumerable.Range(0, viewCount)
            .Select(slot => CreatePlan(viewCount, columns, slot, assignments[slot]))
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

    private static CameraPreviewSlotPlan CreatePlan(
        int viewCount,
        int columns,
        int slot,
        CameraProfile? camera) =>
        viewCount switch
        {
            1 => new CameraPreviewSlotPlan(slot, 0, 0, 1, 1, camera),
            2 => new CameraPreviewSlotPlan(slot, 0, slot, 1, 1, camera),
            3 when slot == 0 => new CameraPreviewSlotPlan(slot, 0, 0, 2, 1, camera),
            3 => new CameraPreviewSlotPlan(slot, slot - 1, 1, 1, 1, camera),
            _ => new CameraPreviewSlotPlan(slot, slot / columns, slot % columns, 1, 1, camera)
        };

    /// <summary>
    /// Chooses a compact landscape grid without enumerating product-specific layouts. Exact
    /// factorisations are preferred, but extremely wide walls are rejected so 8, 16, 32, and
    /// 64 views naturally become 4x2, 4x4, 8x4, and 8x8 respectively.
    /// </summary>
    private static (int Rows, int Columns) DetermineGridDimensions(int viewCount)
    {
        if (viewCount <= 1)
        {
            return (1, 1);
        }
        if (viewCount == 2)
        {
            return (1, 2);
        }
        if (viewCount == 3)
        {
            return (2, 2);
        }

        var maximumRows = (int)Math.Ceiling(Math.Sqrt(viewCount));
        var bestRows = maximumRows;
        var bestColumns = maximumRows;
        var bestUnusedSlots = int.MaxValue;
        var bestAspectPenalty = double.MaxValue;
        const double targetAspectRatio = 16d / 9d;

        for (var rows = 2; rows <= maximumRows; rows++)
        {
            var columns = (int)Math.Ceiling(viewCount / (double)rows);
            if (columns < rows || columns > rows * 2)
            {
                continue;
            }

            var unusedSlots = (rows * columns) - viewCount;
            var aspectPenalty = Math.Abs((columns / (double)rows) - targetAspectRatio);
            if (unusedSlots < bestUnusedSlots ||
                (unusedSlots == bestUnusedSlots && aspectPenalty < bestAspectPenalty))
            {
                bestRows = rows;
                bestColumns = columns;
                bestUnusedSlots = unusedSlots;
                bestAspectPenalty = aspectPenalty;
            }
        }

        return (bestRows, bestColumns);
    }
}
