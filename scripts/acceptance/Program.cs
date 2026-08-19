using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Data.Sqlite;

namespace UnpackVision.Acceptance;

public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("""
                Read-only acceptance snapshot for UnpackVision.

                Required:
                  --database <unpackvision.db>
                  --output <snapshot.json>

                Optional:
                  --workbook <workbook.xlsx>
                  --root <recording root>            Repeat for each trusted storage root.
                  --since <ISO-8601 timestamp>
                  --until <ISO-8601 timestamp>
                  --expected-cameras <1..64>
                  --ffprobe <ffprobe.exe> --probe-media

                The JSON report contains record/asset IDs and aggregate counts only. It never
                emits tracking numbers, credentials, stream URLs, or media/workbook paths.
                """);
            return 0;
        }

        try
        {
            var options = AcceptanceSnapshotOptions.Parse(args);
            var snapshot = await AcceptanceSnapshotCollector.CollectAsync(options);
            var directory = Path.GetDirectoryName(options.OutputPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(options.OutputPath, JsonSerializer.Serialize(snapshot, JsonOptions));
            Console.WriteLine(options.OutputPath);
            return snapshot.DatabaseReadable ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}

public sealed record AcceptanceSnapshotOptions(
    string DatabasePath,
    string OutputPath,
    string? WorkbookPath,
    IReadOnlyList<string> RecordingRoots,
    DateTimeOffset? Since,
    DateTimeOffset? Until,
    int ExpectedCameraCount,
    string? FfprobePath,
    bool ProbeMedia)
{
    public static AcceptanceSnapshotOptions Parse(IReadOnlyList<string> args)
    {
        string? database = null;
        string? output = null;
        string? workbook = null;
        string? ffprobe = null;
        DateTimeOffset? since = null;
        DateTimeOffset? until = null;
        var roots = new List<string>();
        var expectedCameras = 0;
        var probeMedia = false;

        for (var index = 0; index < args.Count; index++)
        {
            var key = args[index];
            string Next()
            {
                if (++index >= args.Count) throw new ArgumentException($"Missing value for {key}");
                return args[index];
            }

            switch (key.ToLowerInvariant())
            {
                case "--database": database = Next(); break;
                case "--output": output = Next(); break;
                case "--workbook": workbook = Next(); break;
                case "--root": roots.Add(Next()); break;
                case "--since": since = DateTimeOffset.Parse(Next(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind); break;
                case "--until": until = DateTimeOffset.Parse(Next(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind); break;
                case "--expected-cameras": expectedCameras = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--ffprobe": ffprobe = Next(); break;
                case "--probe-media": probeMedia = true; break;
                default: throw new ArgumentException($"Unknown option: {key}");
            }
        }

        if (string.IsNullOrWhiteSpace(database)) throw new ArgumentException("--database is required");
        if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("--output is required");
        if (expectedCameras is < 0 or > 64) throw new ArgumentOutOfRangeException(nameof(expectedCameras));
        if (probeMedia && (string.IsNullOrWhiteSpace(ffprobe) || !File.Exists(ffprobe)))
            throw new FileNotFoundException("--probe-media requires an existing --ffprobe executable", ffprobe);

        return new AcceptanceSnapshotOptions(
            Path.GetFullPath(database),
            Path.GetFullPath(output),
            string.IsNullOrWhiteSpace(workbook) ? null : Path.GetFullPath(workbook),
            roots.Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            since,
            until,
            expectedCameras,
            string.IsNullOrWhiteSpace(ffprobe) ? null : Path.GetFullPath(ffprobe),
            probeMedia);
    }
}

public sealed record AcceptanceSnapshot(
    DateTimeOffset CapturedAt,
    bool DatabaseReadable,
    string? DatabaseError,
    int Records,
    int CompletedRecords,
    int FailedRecords,
    int DeletedRecords,
    int UniqueTrackingNumbers,
    int MediaAssets,
    int IndependentMediaAssets,
    int CompositeMediaAssets,
    int CompleteMediaAssets,
    int PartialMediaAssets,
    int FailedMediaAssets,
    int MissingMediaFiles,
    int ZeroByteMediaFiles,
    int OutsideTrustedRootFiles,
    int MediaGaps,
    int UnrecoveredMediaGaps,
    bool WorkbookConfigured,
    bool WorkbookReadable,
    string? WorkbookError,
    int ExcelMarkers,
    int CompletedRecordsMissingExcelMarker,
    int ExcelMarkersWithoutWindowRecord,
    int SyncSucceeded,
    int SyncPending,
    int SyncFailed,
    int RecordsMissingExpectedCameraAssets,
    int ProbedMediaFiles,
    int ProbeFailures,
    int MediaBelowNinetyPercentTargetFps,
    double MaximumAssetDurationDriftMilliseconds,
    IReadOnlyList<string> CompletedRecordIds,
    IReadOnlyList<string> MissingExcelRecordIds,
    IReadOnlyList<string> RecordsWithMissingAssets,
    IReadOnlyList<string> RecordsWithMediaGaps,
    IReadOnlyList<string> ProbeFailureAssetIds);

public static class AcceptanceSnapshotCollector
{
    public static async Task<AcceptanceSnapshot> CollectAsync(AcceptanceSnapshotOptions options, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(options.DatabasePath))
            return Empty($"Database does not exist: {Path.GetFileName(options.DatabasePath)}");

        try
        {
            var records = new List<RecordRow>();
            var assets = new List<AssetRow>();
            var gapRecordIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unrecoveredGaps = 0;
            var syncSucceeded = 0;
            var syncPending = 0;
            var syncFailed = 0;

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = options.DatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = 5
            };
            await using var connection = new SqliteConnection(builder.ToString());
            await connection.OpenAsync(cancellationToken);

            var window = BuildWindowClause(options, "s.scanned_at");
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    SELECT s.id, s.tracking_no, s.state, s.deleted_at
                    FROM scan_records s
                    WHERE 1=1 {window.Sql};
                    """;
                AddWindowParameters(command, options);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    records.Add(new RecordRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), !reader.IsDBNull(3)));
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    SELECT a.id, a.record_id, a.role, a.video_path, a.frames_per_second, a.integrity
                    FROM record_media_assets a
                    JOIN scan_records s ON s.id=a.record_id
                    WHERE 1=1 {window.Sql};
                    """;
                AddWindowParameters(command, options);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    assets.Add(new AssetRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetDouble(4), reader.GetString(5)));
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    SELECT g.record_id, g.recovered
                    FROM media_gaps g JOIN scan_records s ON s.id=g.record_id
                    WHERE 1=1 {window.Sql};
                    """;
                AddWindowParameters(command, options);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    gapRecordIds.Add(reader.GetString(0));
                    if (reader.GetInt64(1) == 0) unrecoveredGaps++;
                }
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    SELECT d.status, COUNT(*)
                    FROM sync_deliveries d JOIN scan_records s ON s.id=d.record_id
                    WHERE 1=1 {window.Sql}
                    GROUP BY d.status;
                    """;
                AddWindowParameters(command, options);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var status = reader.GetString(0);
                    var count = reader.GetInt32(1);
                    if (status.Equals("Succeeded", StringComparison.OrdinalIgnoreCase)) syncSucceeded += count;
                    else if (status.Equals("Failed", StringComparison.OrdinalIgnoreCase)) syncFailed += count;
                    else syncPending += count;
                }
            }

            var activeRecords = records.Where(item => !item.Deleted).ToArray();
            var completed = activeRecords.Where(item => item.State.Equals("Completed", StringComparison.OrdinalIgnoreCase)).ToArray();
            var completedIds = completed.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var workbookConfigured = options.WorkbookPath is not null;
            var workbookReadable = !workbookConfigured;
            string? workbookError = null;
            var excelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (workbookConfigured)
            {
                try
                {
                    if (!File.Exists(options.WorkbookPath)) throw new FileNotFoundException("Workbook does not exist");
                    excelIds = WorkbookMarkerReader.ReadRecordIds(options.WorkbookPath);
                    workbookReadable = true;
                }
                catch (Exception ex)
                {
                    workbookError = SanitizeError(ex.Message, options);
                }
            }
            var missingExcel = options.WorkbookPath is null
                ? []
                : completedIds.Except(excelIds, StringComparer.OrdinalIgnoreCase).Order().ToArray();
            var excessExcel = excelIds.Except(records.Select(item => item.Id), StringComparer.OrdinalIgnoreCase).Count();

            var missingAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var missingFiles = 0;
            var zeroFiles = 0;
            var outsideRoots = 0;
            foreach (var asset in assets)
            {
                if (!File.Exists(asset.Path))
                {
                    missingFiles++;
                    missingAssets.Add(asset.RecordId);
                    continue;
                }
                if (new FileInfo(asset.Path).Length == 0)
                {
                    zeroFiles++;
                    missingAssets.Add(asset.RecordId);
                }
                if (options.RecordingRoots.Count > 0 && !IsUnderAnyRoot(asset.Path, options.RecordingRoots)) outsideRoots++;
            }

            var recordsMissingCameraAssets = 0;
            if (options.ExpectedCameraCount > 0)
            {
                foreach (var record in completed)
                {
                    var count = assets.Count(item => item.RecordId.Equals(record.Id, StringComparison.OrdinalIgnoreCase) &&
                                                     !item.Role.Equals("Composite", StringComparison.OrdinalIgnoreCase));
                    if (count < options.ExpectedCameraCount) recordsMissingCameraAssets++;
                }
            }

            var probes = options.ProbeMedia
                ? await MediaProbe.ProbeAsync(assets.Where(item => File.Exists(item.Path) && new FileInfo(item.Path).Length > 0), options.FfprobePath!, cancellationToken)
                : [];
            var probeFailures = probes.Where(item => !item.Success).ToArray();
            var belowFps = probes.Count(item => item.Success && item.TargetFps > 0 && item.ActualFps < item.TargetFps * 0.9);
            var maxDrift = probes.Where(item => item.Success).GroupBy(item => item.RecordId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Max(item => item.DurationMilliseconds) - group.Min(item => item.DurationMilliseconds))
                .DefaultIfEmpty(0).Max();

            return new AcceptanceSnapshot(
                DateTimeOffset.Now,
                true,
                null,
                activeRecords.Length,
                completed.Length,
                activeRecords.Count(item => item.State.Equals("Failed", StringComparison.OrdinalIgnoreCase)),
                records.Count(item => item.Deleted),
                activeRecords.Select(item => item.TrackingNo).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                assets.Count,
                assets.Count(item => !item.Role.Equals("Composite", StringComparison.OrdinalIgnoreCase)),
                assets.Count(item => item.Role.Equals("Composite", StringComparison.OrdinalIgnoreCase)),
                assets.Count(item => item.Integrity.Equals("Complete", StringComparison.OrdinalIgnoreCase)),
                assets.Count(item => item.Integrity.Equals("Partial", StringComparison.OrdinalIgnoreCase)),
                assets.Count(item => item.Integrity.Equals("Failed", StringComparison.OrdinalIgnoreCase)),
                missingFiles,
                zeroFiles,
                outsideRoots,
                gapRecordIds.Count,
                unrecoveredGaps,
                workbookConfigured,
                workbookReadable,
                workbookError,
                excelIds.Count,
                missingExcel.Length,
                excessExcel,
                syncSucceeded,
                syncPending,
                syncFailed,
                recordsMissingCameraAssets,
                probes.Count,
                probeFailures.Length,
                belowFps,
                maxDrift,
                completedIds.Order().ToArray(),
                missingExcel,
                missingAssets.Order().ToArray(),
                gapRecordIds.Order().ToArray(),
                probeFailures.Select(item => item.AssetId).Order().ToArray());
        }
        catch (Exception ex)
        {
            return Empty(SanitizeError(ex.Message, options));
        }
    }

    private static AcceptanceSnapshot Empty(string error) => new(
        CapturedAt: DateTimeOffset.Now,
        DatabaseReadable: false,
        DatabaseError: error,
        Records: 0,
        CompletedRecords: 0,
        FailedRecords: 0,
        DeletedRecords: 0,
        UniqueTrackingNumbers: 0,
        MediaAssets: 0,
        IndependentMediaAssets: 0,
        CompositeMediaAssets: 0,
        CompleteMediaAssets: 0,
        PartialMediaAssets: 0,
        FailedMediaAssets: 0,
        MissingMediaFiles: 0,
        ZeroByteMediaFiles: 0,
        OutsideTrustedRootFiles: 0,
        MediaGaps: 0,
        UnrecoveredMediaGaps: 0,
        WorkbookConfigured: false,
        WorkbookReadable: true,
        WorkbookError: null,
        ExcelMarkers: 0,
        CompletedRecordsMissingExcelMarker: 0,
        ExcelMarkersWithoutWindowRecord: 0,
        SyncSucceeded: 0,
        SyncPending: 0,
        SyncFailed: 0,
        RecordsMissingExpectedCameraAssets: 0,
        ProbedMediaFiles: 0,
        ProbeFailures: 0,
        MediaBelowNinetyPercentTargetFps: 0,
        MaximumAssetDurationDriftMilliseconds: 0,
        CompletedRecordIds: [],
        MissingExcelRecordIds: [],
        RecordsWithMissingAssets: [],
        RecordsWithMediaGaps: [],
        ProbeFailureAssetIds: []);

    private static (string Sql, bool HasSince, bool HasUntil) BuildWindowClause(AcceptanceSnapshotOptions options, string column)
    {
        var clauses = new List<string>();
        if (options.Since.HasValue) clauses.Add($"AND julianday({column}) >= julianday($since)");
        if (options.Until.HasValue) clauses.Add($"AND julianday({column}) <= julianday($until)");
        return (string.Join(' ', clauses), options.Since.HasValue, options.Until.HasValue);
    }

    private static void AddWindowParameters(SqliteCommand command, AcceptanceSnapshotOptions options)
    {
        if (options.Since.HasValue) command.Parameters.AddWithValue("$since", options.Since.Value.ToString("O", CultureInfo.InvariantCulture));
        if (options.Until.HasValue) command.Parameters.AddWithValue("$until", options.Until.Value.ToString("O", CultureInfo.InvariantCulture));
    }

    private static bool IsUnderAnyRoot(string path, IReadOnlyList<string> roots)
    {
        var full = Path.GetFullPath(path);
        return roots.Any(root =>
        {
            var normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                   full.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string SanitizeError(string message, AcceptanceSnapshotOptions options)
    {
        var sanitized = message.Replace(options.DatabasePath, "<database>", StringComparison.OrdinalIgnoreCase);
        if (options.WorkbookPath is not null)
            sanitized = sanitized.Replace(options.WorkbookPath, "<workbook>", StringComparison.OrdinalIgnoreCase);
        foreach (var root in options.RecordingRoots)
            sanitized = sanitized.Replace(root, "<recording-root>", StringComparison.OrdinalIgnoreCase);
        sanitized = sanitized.ReplaceLineEndings(" ");
        return sanitized.Length <= 500 ? sanitized : sanitized[..500];
    }

    private sealed record RecordRow(string Id, string TrackingNo, string State, bool Deleted);
    private sealed record AssetRow(string Id, string RecordId, string Role, string Path, double TargetFps, string Integrity);

    private static class WorkbookMarkerReader
    {
        public static HashSet<string> ReadRecordIds(string workbookPath)
        {
            var temporary = Path.Combine(Path.GetTempPath(), $"unpackvision-acceptance-{Guid.NewGuid():N}.xlsx");
            try
            {
                using (var source = new FileStream(workbookPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var destination = File.Create(temporary))
                    source.CopyTo(destination);
                using var document = SpreadsheetDocument.Open(temporary, false);
                var workbookPart = document.WorkbookPart ?? throw new InvalidDataException("Workbook part is missing");
                var sheet = workbookPart.Workbook?.Sheets?.Elements<Sheet>()
                    .FirstOrDefault(item => string.Equals(item.Name?.Value, "__UnpackVisionSync", StringComparison.Ordinal));
                if (sheet?.Id?.Value is null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var part = (WorksheetPart)workbookPart.GetPartById(sheet.Id.Value);
                var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;
                return part.Worksheet?.GetFirstChild<SheetData>()?.Elements<Row>().Skip(1)
                    .Select(row => ReadCell(row.Elements<Cell>().FirstOrDefault(cell => CellColumn(cell.CellReference?.Value) == "A"), sharedStrings))
                    .Where(value => Guid.TryParse(value, out _))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            finally
            {
                try { File.Delete(temporary); } catch { }
            }
        }

        private static string ReadCell(Cell? cell, SharedStringTable? sharedStrings)
        {
            if (cell is null) return string.Empty;
            if (cell.DataType?.Value == CellValues.SharedString && int.TryParse(cell.CellValue?.Text, out var index))
                return sharedStrings?.Elements<SharedStringItem>().ElementAtOrDefault(index)?.InnerText ?? string.Empty;
            return cell.InlineString?.InnerText ?? cell.CellValue?.Text ?? string.Empty;
        }

        private static string CellColumn(string? reference) =>
            new((reference ?? string.Empty).TakeWhile(char.IsLetter).ToArray());
    }

    private static class MediaProbe
    {
        public static async Task<IReadOnlyList<MediaProbeResult>> ProbeAsync(IEnumerable<AssetRow> assets, string ffprobePath, CancellationToken cancellationToken)
        {
            var results = new List<MediaProbeResult>();
            foreach (var asset in assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(await ProbeOneAsync(asset, ffprobePath, cancellationToken));
            }
            return results;
        }

        private static async Task<MediaProbeResult> ProbeOneAsync(AssetRow asset, string ffprobePath, CancellationToken cancellationToken)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffprobePath,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                process.StartInfo.ArgumentList.Add("-v");
                process.StartInfo.ArgumentList.Add("error");
                process.StartInfo.ArgumentList.Add("-select_streams");
                process.StartInfo.ArgumentList.Add("v:0");
                process.StartInfo.ArgumentList.Add("-show_entries");
                process.StartInfo.ArgumentList.Add("stream=avg_frame_rate:format=duration");
                process.StartInfo.ArgumentList.Add("-of");
                process.StartInfo.ArgumentList.Add("json");
                process.StartInfo.ArgumentList.Add(asset.Path);
                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                var error = await process.StandardError.ReadToEndAsync(cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                await process.WaitForExitAsync(timeout.Token);
                if (process.ExitCode != 0) return new(asset.Id, asset.RecordId, false, asset.TargetFps, 0, 0, Trim(error));

                using var json = JsonDocument.Parse(output);
                var stream = json.RootElement.GetProperty("streams").EnumerateArray().FirstOrDefault();
                var frameRate = stream.ValueKind == JsonValueKind.Object && stream.TryGetProperty("avg_frame_rate", out var rate)
                    ? ParseRate(rate.GetString()) : 0;
                var duration = json.RootElement.GetProperty("format").TryGetProperty("duration", out var durationValue) &&
                               double.TryParse(durationValue.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                    ? seconds * 1000 : 0;
                return new(asset.Id, asset.RecordId, frameRate > 0 && duration > 0, asset.TargetFps, frameRate, duration, null);
            }
            catch (Exception ex)
            {
                return new(asset.Id, asset.RecordId, false, asset.TargetFps, 0, 0, Trim(ex.Message));
            }
        }

        private static double ParseRate(string? value)
        {
            var parts = (value ?? string.Empty).Split('/');
            if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) && denominator != 0)
                return numerator / denominator;
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        }

        private static string Trim(string value) => value.Length <= 300 ? value : value[..300];
    }

    private sealed record MediaProbeResult(
        string AssetId,
        string RecordId,
        bool Success,
        double TargetFps,
        double ActualFps,
        double DurationMilliseconds,
        string? Error);
}
