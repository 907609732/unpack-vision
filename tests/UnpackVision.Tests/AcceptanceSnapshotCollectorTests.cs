using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Data.Sqlite;
using UnpackVision.Acceptance;

namespace UnpackVision.Tests;

public sealed class AcceptanceSnapshotCollectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"UnpackVisionAcceptance-{Guid.NewGuid():N}");

    [Fact]
    public async Task Collector_ReconcilesDatabaseMediaAndExcelWithoutLeakingBusinessValues()
    {
        Directory.CreateDirectory(_root);
        var database = Path.Combine(_root, "acceptance.db");
        var workbook = Path.Combine(_root, "acceptance.xlsx");
        var video = Path.Combine(_root, "recording.mp4");
        await File.WriteAllBytesAsync(video, [1, 2, 3, 4]);
        var recordId = Guid.NewGuid().ToString("D");
        await CreateDatabaseAsync(database, recordId, video);
        CreateWorkbook(workbook, recordId);

        var snapshot = await AcceptanceSnapshotCollector.CollectAsync(new AcceptanceSnapshotOptions(
            database,
            Path.Combine(_root, "snapshot.json"),
            workbook,
            [_root],
            DateTimeOffset.Now.AddMinutes(-5),
            DateTimeOffset.Now.AddMinutes(5),
            1,
            null,
            false));

        Assert.True(snapshot.DatabaseReadable, snapshot.DatabaseError);
        Assert.Equal(1, snapshot.CompletedRecords);
        Assert.Equal(1, snapshot.UniqueTrackingNumbers);
        Assert.Equal(1, snapshot.IndependentMediaAssets);
        Assert.Equal(0, snapshot.MissingMediaFiles);
        Assert.Equal(0, snapshot.CompletedRecordsMissingExcelMarker);
        Assert.Equal(0, snapshot.RecordsMissingExpectedCameraAssets);

        var reportJson = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("PRIVATE-TRACKING-123", reportJson, StringComparison.Ordinal);
        Assert.DoesNotContain(video, reportJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(recordId, reportJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Collector_FlagsMissingMediaAndExpectedCameraCount()
    {
        Directory.CreateDirectory(_root);
        var database = Path.Combine(_root, "missing.db");
        var recordId = Guid.NewGuid().ToString("D");
        await CreateDatabaseAsync(database, recordId, Path.Combine(_root, "missing.mp4"));

        var snapshot = await AcceptanceSnapshotCollector.CollectAsync(new AcceptanceSnapshotOptions(
            database,
            Path.Combine(_root, "snapshot.json"),
            null,
            [_root],
            DateTimeOffset.Now.AddMinutes(-5),
            DateTimeOffset.Now.AddMinutes(5),
            8,
            null,
            false));

        Assert.True(snapshot.DatabaseReadable, snapshot.DatabaseError);
        Assert.Equal(1, snapshot.MissingMediaFiles);
        Assert.Equal(1, snapshot.RecordsMissingExpectedCameraAssets);
        Assert.Contains(recordId, snapshot.RecordsWithMissingAssets, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task CreateDatabaseAsync(string path, string recordId, string videoPath)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE scan_records (
                id TEXT PRIMARY KEY,
                tracking_no TEXT NOT NULL,
                state TEXT NOT NULL,
                scanned_at TEXT NOT NULL,
                deleted_at TEXT NULL
            );
            CREATE TABLE record_media_assets (
                id TEXT PRIMARY KEY,
                record_id TEXT NOT NULL,
                role TEXT NOT NULL,
                video_path TEXT NOT NULL,
                frames_per_second REAL NOT NULL,
                integrity TEXT NOT NULL
            );
            CREATE TABLE media_gaps (record_id TEXT NOT NULL, recovered INTEGER NOT NULL);
            CREATE TABLE sync_deliveries (record_id TEXT NOT NULL, status TEXT NOT NULL);
            INSERT INTO scan_records(id, tracking_no, state, scanned_at, deleted_at)
                VALUES ($recordId, 'PRIVATE-TRACKING-123', 'Completed', $scannedAt, NULL);
            INSERT INTO record_media_assets(id, record_id, role, video_path, frames_per_second, integrity)
                VALUES ($assetId, $recordId, 'Primary', $videoPath, 15, 'Complete');
            INSERT INTO sync_deliveries(record_id, status) VALUES ($recordId, 'Succeeded');
            """;
        command.Parameters.AddWithValue("$recordId", recordId);
        command.Parameters.AddWithValue("$assetId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$videoPath", videoPath);
        command.Parameters.AddWithValue("$scannedAt", DateTimeOffset.Now.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static void CreateWorkbook(string path, string recordId)
    {
        using var document = SpreadsheetDocument.Create(path, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var part = workbookPart.AddNewPart<WorksheetPart>();
        part.Worksheet = new Worksheet(new SheetData(
            new Row(new Cell
            {
                CellReference = "A1",
                DataType = CellValues.InlineString,
                InlineString = new InlineString(new DocumentFormat.OpenXml.Spreadsheet.Text("RecordId"))
            }) { RowIndex = 1 },
            new Row(new Cell
            {
                CellReference = "A2",
                DataType = CellValues.InlineString,
                InlineString = new InlineString(new DocumentFormat.OpenXml.Spreadsheet.Text(recordId))
            }) { RowIndex = 2 }));
        workbookPart.Workbook.AppendChild(new Sheets(new Sheet
        {
            Id = workbookPart.GetIdOfPart(part),
            SheetId = 1,
            Name = "__UnpackVisionSync",
            State = SheetStateValues.VeryHidden
        }));
        workbookPart.Workbook.Save();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
