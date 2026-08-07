using System.Text.Json;
using System.Text.Json.Serialization;
using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

public sealed class PortableRecordCatalog : IPortableRecordCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string _indexRoot;
    private readonly string _recordsRoot;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public PortableRecordCatalog(string recordingRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingRoot);
        RecordingRoot = Path.GetFullPath(recordingRoot);
        _indexRoot = Path.Combine(RecordingRoot, ".unpackvision");
        _recordsRoot = Path.Combine(_indexRoot, "records");
    }

    public string RecordingRoot { get; }
    public string IndexRoot => _indexRoot;

    public async Task<WorkspaceManifest> EnsureWorkspaceAsync(
        Guid? preferredWorkspaceId = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_recordsRoot);
        TryMarkHidden(_indexRoot);
        var path = Path.Combine(_indexRoot, "workspace.json");
        if (File.Exists(path))
        {
            await using var existing = File.OpenRead(path);
            var manifest = await JsonSerializer.DeserializeAsync<WorkspaceManifest>(
                existing,
                JsonOptions,
                cancellationToken);
            if (manifest is not null && manifest.WorkspaceId != Guid.Empty)
            {
                return manifest;
            }
        }

        var created = new WorkspaceManifest
        {
            WorkspaceId = preferredWorkspaceId is { } value && value != Guid.Empty
                ? value
                : Guid.NewGuid()
        };
        await WriteJsonAtomicAsync(path, created, cancellationToken);
        return created;
    }

    public async Task WriteAsync(
        ScanRecord record,
        SyncDelivery? delivery = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await EnsureWorkspaceAsync(cancellationToken: cancellationToken);
        var portable = new PortableScanRecord
        {
            Id = record.Id,
            TrackingNo = record.TrackingNo,
            Workflow = record.Workflow,
            State = record.State,
            ScannedAt = record.ScannedAt,
            RecordingStartedAt = record.RecordingStartedAt,
            RecordingEndedAt = record.RecordingEndedAt,
            RelativeVideoPath = ToRelativePath(record.VideoPath),
            RelativeSnapshots = record.Snapshots
                .Select(ToRelativePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .ToArray(),
            CameraId = record.CameraId,
            StationId = record.StationId,
            DuplicateOf = record.DuplicateOf,
            PlatformMatchStatus = record.PlatformMatchStatus,
            Note = record.Note,
            NoteUpdatedAt = record.NoteUpdatedAt,
            Tags = record.Tags.ToArray(),
            FailureReason = record.FailureReason,
            ExcelSyncStatus = delivery?.Status.ToString(),
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt
        };
        await WriteJsonAtomicAsync(
            Path.Combine(_recordsRoot, $"{record.Id:D}.json"),
            portable,
            cancellationToken);
    }

    public Task DeleteAsync(Guid recordId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(_recordsRoot, $"{recordId:D}.json");
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<RecoveryItem>> ReadAllAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_recordsRoot))
        {
            return [];
        }
        var items = new List<RecoveryItem>();
        foreach (var path in Directory.EnumerateFiles(_recordsRoot, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(path);
                var record = await JsonSerializer.DeserializeAsync<PortableScanRecord>(
                    stream,
                    JsonOptions,
                    cancellationToken);
                if (record is null || record.Id == Guid.Empty || string.IsNullOrWhiteSpace(record.TrackingNo))
                {
                    items.Add(new RecoveryItem(RecoveryItemKind.Invalid, "便携记录缺少必要字段", null, path));
                    continue;
                }
                var kind = string.IsNullOrWhiteSpace(record.RelativeVideoPath) ||
                           File.Exists(ResolveRelativePath(record.RelativeVideoPath))
                    ? RecoveryItemKind.Complete
                    : RecoveryItemKind.MissingVideo;
                items.Add(new RecoveryItem(kind, kind == RecoveryItemKind.Complete
                    ? $"可恢复：{record.TrackingNo}"
                    : $"录像缺失：{record.TrackingNo}", record, path));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                items.Add(new RecoveryItem(
                    RecoveryItemKind.Invalid,
                    $"索引损坏：{Path.GetFileName(path)}（{ex.Message}）",
                    null,
                    path));
            }
        }
        return items;
    }

    public string? ToRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(RecordingRoot, fullPath);
        return IsSafeRelativePath(relative) ? relative.Replace('\\', '/') : null;
    }

    public string ResolveRelativePath(string relativePath)
    {
        if (!IsSafeRelativePath(relativePath))
        {
            throw new InvalidDataException("便携索引包含越界路径");
        }
        var full = Path.GetFullPath(Path.Combine(RecordingRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(RecordingRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(full, RecordingRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("便携索引路径超出录像目录");
        }
        return full;
    }

    private static bool IsSafeRelativePath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !Path.IsPathRooted(path) &&
        !path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part == "..");

    private async Task WriteJsonAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = Path.Combine(
                Path.GetDirectoryName(path)!,
                $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = new FileStream(
                                 temporary,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 4096,
                                 FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
                File.Move(temporary, path, true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static void TryMarkHidden(string path)
    {
        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Hidden is cosmetic. Index creation must still work on non-Windows file systems.
        }
    }
}
