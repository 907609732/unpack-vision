using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Data.Sqlite;
using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

public sealed partial class WorkspaceRecoveryService(
    IScanRecordRepository repository,
    StorageOptions storageOptions) : IWorkspaceRecoveryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    [GeneratedRegex(
        "^(?<tracking>.+?)_(?<start>\\d{14})_(?<end>\\d{14})(?:_异常(?:-(?<tags>.+?))?)?(?:_[0-9A-Fa-f]{8})?\\.mp4$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RecordingPattern();

    public async Task<RecoveryPreview> PreviewAsync(
        string recordingRoot,
        string? workbookPath,
        CancellationToken cancellationToken = default)
    {
        var catalog = new PortableRecordCatalog(recordingRoot);
        var manifest = await catalog.EnsureWorkspaceAsync(cancellationToken: cancellationToken);
        var items = (await catalog.ReadAllAsync(cancellationToken)).ToList();
        var indexedVideoPaths = items
            .Where(item => item.Record?.RelativeVideoPath is not null)
            .Select(item => NormalizeRelative(item.Record!.RelativeVideoPath!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(recordingRoot))
        {
            foreach (var videoPath in Directory.EnumerateFiles(
                         recordingRoot,
                         "*.mp4",
                         SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (videoPath.Contains(
                        $"{Path.DirectorySeparatorChar}.unpackvision{Path.DirectorySeparatorChar}",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var relative = NormalizeRelative(Path.GetRelativePath(recordingRoot, videoPath));
                if (indexedVideoPaths.Contains(relative) ||
                    !TryCreateLegacyRecord(manifest.WorkspaceId, recordingRoot, videoPath, out var record))
                {
                    continue;
                }
                items.Add(new RecoveryItem(
                    RecoveryItemKind.FileNameOnly,
                    $"从录像文件名恢复：{record.TrackingNo}",
                    record,
                    videoPath));
            }
        }

        if (!string.IsNullOrWhiteSpace(workbookPath) && File.Exists(workbookPath))
        {
            MergeWorkbookRows(items, recordingRoot, workbookPath, cancellationToken);
        }

        for (var index = 0; index < items.Count; index++)
        {
            var portable = items[index].Record;
            if (portable is null)
            {
                continue;
            }
            var existing = await repository.GetAsync(portable.Id, cancellationToken);
            if (existing is not null && (
                    existing.UpdatedAt != portable.UpdatedAt ||
                    !string.Equals(existing.Note, portable.Note, StringComparison.Ordinal) ||
                    existing.Tags.Count != portable.Tags.Count))
            {
                items[index] = items[index] with
                {
                    Kind = RecoveryItemKind.Conflict,
                    Description = $"需要合并：{portable.TrackingNo}"
                };
            }
        }

        return new RecoveryPreview
        {
            WorkspaceId = manifest.WorkspaceId,
            Items = items
                .OrderBy(item => item.Record?.ScannedAt ?? DateTimeOffset.MinValue)
                .ToArray()
        };
    }

    public async Task<RecoveryResult> RecoverAsync(
        RecoveryPreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        await BackupDatabaseAsync(cancellationToken);
        var conflicts = new List<RecoveryConflict>();
        var added = 0;
        var updated = 0;
        var skipped = 0;

        foreach (var item in preview.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Record is null || item.Kind == RecoveryItemKind.Invalid)
            {
                skipped++;
                continue;
            }
            var record = ToScanRecord(item.Record);
            var existing = await repository.GetAsync(record.Id, cancellationToken);
            if (existing is null)
            {
                if (await repository.MergeRecoveredAsync(record, cancellationToken))
                {
                    added++;
                }
                else
                {
                    skipped++;
                }
                continue;
            }

            var changed = await repository.MergeRecoveredAsync(record, cancellationToken);
            if (changed)
            {
                updated++;
                conflicts.Add(new RecoveryConflict(
                    record.Id,
                    "record",
                    "核心字段按 UpdatedAt 合并，备注按 NoteUpdatedAt 合并，标签按分配记录合并",
                    DateTimeOffset.Now));
            }
            else
            {
                skipped++;
            }
        }

        var reportRoot = Path.Combine(
            Path.GetFullPath(storageOptions.RecordingRoot),
            ".unpackvision",
            "recovery-reports");
        Directory.CreateDirectory(reportRoot);
        var reportPath = Path.Combine(reportRoot, $"recovery-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(
            reportPath,
            JsonSerializer.Serialize(new
            {
                recoveredAt = DateTimeOffset.Now,
                preview.WorkspaceId,
                added,
                updated,
                skipped,
                conflicts
            }, JsonOptions),
            cancellationToken);
        return new RecoveryResult(added, updated, skipped, reportPath, conflicts);
    }

    private ScanRecord ToScanRecord(PortableScanRecord portable)
    {
        var root = Path.GetFullPath(storageOptions.RecordingRoot);
        return new ScanRecord
        {
            Id = portable.Id,
            TrackingNo = portable.TrackingNo,
            Workflow = portable.Workflow,
            State = portable.State,
            ScannedAt = portable.ScannedAt,
            RecordingStartedAt = portable.RecordingStartedAt,
            RecordingEndedAt = portable.RecordingEndedAt,
            VideoPath = ResolveRelative(root, portable.RelativeVideoPath),
            Snapshots = portable.RelativeSnapshots
                .Select(path => ResolveRelative(root, path))
                .Where(path => path is not null)
                .Cast<string>()
                .ToArray(),
            CameraId = portable.CameraId,
            StationId = portable.StationId,
            DuplicateOf = portable.DuplicateOf,
            PlatformMatchStatus = portable.PlatformMatchStatus,
            Note = portable.Note,
            NoteUpdatedAt = portable.NoteUpdatedAt,
            Tags = portable.Tags,
            FailureReason = portable.FailureReason,
            CreatedAt = portable.CreatedAt,
            UpdatedAt = portable.UpdatedAt
        };
    }

    private static string? ResolveRelative(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
            relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(part => part == ".."))
        {
            return null;
        }
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? full
            : null;
    }

    private static bool TryCreateLegacyRecord(
        Guid workspaceId,
        string recordingRoot,
        string videoPath,
        out PortableScanRecord record)
    {
        record = new PortableScanRecord();
        var match = RecordingPattern().Match(Path.GetFileName(videoPath));
        if (!match.Success ||
            !DateTime.TryParseExact(match.Groups["start"].Value, "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var start) ||
            !DateTime.TryParseExact(match.Groups["end"].Value, "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var end) ||
            end < start)
        {
            return false;
        }
        var relative = NormalizeRelative(Path.GetRelativePath(recordingRoot, videoPath));
        var id = DeterministicGuid($"{workspaceId:D}|{relative}");
        var tagNames = match.Groups["tags"].Success
            ? match.Groups["tags"].Value.Split('-', StringSplitOptions.RemoveEmptyEntries)
                .Where(name => !name.StartsWith("等", StringComparison.Ordinal))
                .ToArray()
            : [];
        var tags = tagNames.Select((name, index) => new RecordTagAssignment
        {
            Id = DeterministicGuid($"{id:D}|tag|{index}|{name}"),
            RecordId = id,
            TagId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8],
            TagName = name,
            TaggedAt = new DateTimeOffset(start).AddSeconds(index),
            Source = "filename-recovery"
        }).ToArray();
        var workflow = videoPath.Contains(
            $"{Path.DirectorySeparatorChar}Packing{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? WorkflowMode.Packing
            : WorkflowMode.Unpacking;
        record = new PortableScanRecord
        {
            Id = id,
            TrackingNo = match.Groups["tracking"].Value,
            Workflow = workflow,
            State = RecordingState.Imported,
            ScannedAt = new DateTimeOffset(start),
            RecordingStartedAt = new DateTimeOffset(start),
            RecordingEndedAt = new DateTimeOffset(end),
            RelativeVideoPath = relative,
            PlatformMatchStatus = "恢复导入",
            Tags = tags,
            CreatedAt = new DateTimeOffset(start),
            UpdatedAt = File.GetLastWriteTimeUtc(videoPath)
        };
        return true;
    }

    private static Guid DeterministicGuid(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static string NormalizeRelative(string path) => path.Replace('\\', '/');

    private static void MergeWorkbookRows(
        List<RecoveryItem> items,
        string recordingRoot,
        string workbookPath,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = SpreadsheetDocument.Open(workbookPath, false);
            var workbookPart = document.WorkbookPart;
            var sheets = workbookPart?.Workbook?.Sheets?.Elements<Sheet>().ToArray() ?? [];
            var mainSheet = sheets.FirstOrDefault(sheet =>
                string.Equals(sheet.Name?.Value, "退货扫码单号", StringComparison.Ordinal));
            var syncSheet = sheets.FirstOrDefault(sheet =>
                string.Equals(sheet.Name?.Value, WorkbookTemplateService.SyncSheetName, StringComparison.Ordinal));
            if (mainSheet is null || syncSheet is null || workbookPart is null)
            {
                return;
            }
            var mainPart = (WorksheetPart)workbookPart.GetPartById(mainSheet.Id!);
            var syncPart = (WorksheetPart)workbookPart.GetPartById(syncSheet.Id!);
            var mainRows = mainPart.Worksheet?.GetFirstChild<SheetData>()?.Elements<Row>()
                .ToDictionary(row => row.RowIndex?.Value ?? 0U) ?? [];
            var shared = workbookPart.SharedStringTablePart?.SharedStringTable;
            foreach (var marker in syncPart.Worksheet?.GetFirstChild<SheetData>()?.Elements<Row>().Skip(1) ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = marker.Elements<Cell>().Select(cell => ReadCell(cell, shared)).ToArray();
                if (values.Length < 2 || !Guid.TryParse(values[0], out var recordId) ||
                    !uint.TryParse(values[1], CultureInfo.InvariantCulture, out var targetRow) ||
                    !mainRows.TryGetValue(targetRow, out var row))
                {
                    continue;
                }
                if (items.Any(item => item.Record?.Id == recordId))
                {
                    continue;
                }
                var cells = row.Elements<Cell>().ToDictionary(
                    cell => new string((cell.CellReference?.Value ?? string.Empty).TakeWhile(char.IsLetter).ToArray()),
                    cell => ReadCell(cell, shared),
                    StringComparer.OrdinalIgnoreCase);
                if (!cells.TryGetValue("B", out var tracking) || string.IsNullOrWhiteSpace(tracking))
                {
                    continue;
                }
                var match = items.FirstOrDefault(item =>
                    item.Kind == RecoveryItemKind.FileNameOnly &&
                    string.Equals(item.Record?.TrackingNo, tracking, StringComparison.OrdinalIgnoreCase));
                var portable = match?.Record ?? new PortableScanRecord
                {
                    Id = recordId,
                    TrackingNo = tracking,
                    Workflow = WorkflowMode.Unpacking,
                    State = RecordingState.Imported,
                    ScannedAt = ParseExcelDate(cells.GetValueOrDefault("A")) ?? DateTimeOffset.Now,
                    PlatformMatchStatus = "Excel恢复",
                    CreatedAt = DateTimeOffset.Now,
                    UpdatedAt = DateTimeOffset.Now
                };
                portable.Id = recordId;
                if (match is not null)
                {
                    items.Remove(match);
                }
                items.Add(new RecoveryItem(
                    RecoveryItemKind.ExcelMatched,
                    $"Excel 与录像已关联：{tracking}",
                    portable,
                    workbookPath));
            }
        }
        catch (Exception ex) when (ex is IOException or OpenXmlPackageException or InvalidDataException)
        {
            items.Add(new RecoveryItem(
                RecoveryItemKind.Invalid,
                $"Excel 恢复信息读取失败：{ex.Message}",
                null,
                workbookPath));
        }
    }

    private static DateTimeOffset? ParseExcelDate(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var oa) &&
        oa is >= 1 and <= 2958465
            ? new DateTimeOffset(DateTime.FromOADate(oa))
            : null;

    private static string ReadCell(Cell cell, SharedStringTable? shared)
    {
        if (cell.DataType?.Value == CellValues.SharedString &&
            int.TryParse(cell.CellValue?.Text, out var index))
        {
            return shared?.Elements<SharedStringItem>().ElementAtOrDefault(index)?.InnerText ?? string.Empty;
        }
        return cell.InlineString?.InnerText ?? cell.CellValue?.Text ?? string.Empty;
    }

    private async Task BackupDatabaseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var databasePath = Path.GetFullPath(storageOptions.DatabasePath);
        if (!File.Exists(databasePath))
        {
            return;
        }
        var backupRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UnpackVision",
            "Backups",
            "Recovery");
        Directory.CreateDirectory(backupRoot);
        var backupId = Guid.NewGuid().ToString("N")[..8];
        var backupPath = Path.Combine(
            backupRoot,
            $"unpackvision-{DateTimeOffset.Now:yyyyMMdd-HHmmssfff}-{backupId}.db");
        var sourceConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        var targetConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        await using var source = new SqliteConnection(sourceConnectionString);
        await using var target = new SqliteConnection(targetConnectionString);
        await source.OpenAsync(cancellationToken);
        await target.OpenAsync(cancellationToken);
        source.BackupDatabase(target);
    }
}
