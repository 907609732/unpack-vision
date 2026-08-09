using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

public static class RecordingFileRenameService
{
    public static string? TryRenameLocalRecording(ScanRecord record, string recordingRoot)
    {
        if (record.State != RecordingState.Completed ||
            string.IsNullOrWhiteSpace(record.VideoPath) ||
            record.RecordingStartedAt is null || record.RecordingEndedAt is null ||
            !File.Exists(record.VideoPath) ||
            !IsUnderRoot(record.VideoPath, recordingRoot))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(record.VideoPath)!;
        var expectedName = RecordingFileNameService.BuildFileName(
            record.TrackingNo,
            record.RecordingStartedAt.Value,
            record.RecordingEndedAt.Value,
            record.Tags);
        var expectedPath = Path.Combine(directory, expectedName);
        if (string.Equals(Path.GetFullPath(record.VideoPath), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
        {
            return record.VideoPath;
        }
        if (File.Exists(expectedPath))
        {
            expectedPath = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(expectedName)}_{record.Id.ToString("N")[..8]}.mp4");
        }
        var mediaMoves = record.MediaAssets
            .Where(asset => asset.Role != RecordMediaRole.Primary &&
                            File.Exists(asset.VideoPath) &&
                            IsUnderRoot(asset.VideoPath, recordingRoot))
            .Select(asset => (Asset: asset, Target: RecordingFileNameService.GetAvailableMediaPath(
                expectedPath,
                asset.Role,
                asset.DisplayName,
                record.Id)))
            .ToArray();
        File.Move(record.VideoPath, expectedPath, false);
        foreach (var move in mediaMoves)
        {
            if (!string.Equals(Path.GetFullPath(move.Asset.VideoPath), Path.GetFullPath(move.Target), StringComparison.OrdinalIgnoreCase))
            {
                File.Move(move.Asset.VideoPath, move.Target, false);
                move.Asset.VideoPath = move.Target;
                move.Asset.UpdatedAt = DateTimeOffset.Now;
            }
        }
        var primaryAsset = record.MediaAssets.FirstOrDefault(asset => asset.Role == RecordMediaRole.Primary);
        if (primaryAsset is not null)
        {
            primaryAsset.VideoPath = expectedPath;
            primaryAsset.UpdatedAt = DateTimeOffset.Now;
        }
        return expectedPath;
    }

    public static bool IsUnderRoot(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }
}
