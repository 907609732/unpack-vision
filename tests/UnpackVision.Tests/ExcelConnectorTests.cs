using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using UnpackVision.Core;
using UnpackVision.Infrastructure;

namespace UnpackVision.Tests;

public sealed class ExcelConnectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"UnpackVisionExcel-{Guid.NewGuid():N}");

    [Fact]
    public async Task AppendsTargetColumnsAndIsIdempotent()
    {
        Directory.CreateDirectory(_root);
        var workbook = Path.Combine(_root, "returns.xlsx");
        CreateWorkbook(workbook);
        var video = Path.Combine(_root, "SF001.mp4");
        await File.WriteAllBytesAsync(video, [1, 2, 3, 4]);
        var connector = new ExcelConnector(new ExcelConnectorOptions
        {
            WorkbookPath = workbook,
            BackupRoot = Path.Combine(_root, "backups")
        });
        var record = new ScanRecord
        {
            TrackingNo = "00123-SF",
            State = RecordingState.Completed,
            ScannedAt = new DateTimeOffset(2026, 7, 19, 9, 30, 0, TimeSpan.FromHours(8)),
            VideoPath = video,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now
        };

        var first = await connector.PushRecordAsync(record);
        var second = await connector.PushRecordAsync(record);

        Assert.Equal(first.ExternalId, second.ExternalId);
        using var document = SpreadsheetDocument.Open(workbook, false);
        var workbookPart = document.WorkbookPart!;
        var workbookRoot = workbookPart.Workbook ?? throw new InvalidDataException();
        var sheet = workbookRoot.Sheets!.Elements<Sheet>().Single(item => item.Name == "退货扫码单号");
        var part = (WorksheetPart)workbookPart.GetPartById(sheet.Id!);
        var worksheet = part.Worksheet ?? throw new InvalidDataException();
        var rows = worksheet.GetFirstChild<SheetData>()!.Elements<Row>().ToList();
        Assert.Equal(4, rows.Count);
        var cells = rows[^1].Elements<Cell>().ToDictionary(cell => cell.CellReference!.Value![0]);
        Assert.Equal(CellValues.Number, cells['A'].DataType?.Value);
        Assert.Equal(
            record.ScannedAt.LocalDateTime.ToOADate(),
            double.Parse(cells['A'].CellValue!.Text, CultureInfo.InvariantCulture),
            10);
        Assert.Equal(1U, cells['A'].StyleIndex?.Value);
        Assert.Equal(58U, GetCellFormat(workbookPart, cells['A']).NumberFormatId?.Value);
        Assert.Equal("00123-SF", cells['B'].InlineString!.InnerText);
        Assert.Contains("'店口五金-拼多多'!B:B", cells['C'].CellFormula!.Text, StringComparison.Ordinal);
        Assert.Null(cells['D'].CellValue);
        var syncSheet = workbookRoot.Sheets!.Elements<Sheet>().Single(item => item.Name == "__UnpackVisionSync");
        Assert.Equal(SheetStateValues.VeryHidden, syncSheet.State?.Value);
        Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(_root, "backups"), "*.xlsx").Count());
    }

    [Fact]
    public async Task RepairsExistingUnformattedDateWithoutChangingItsValue()
    {
        Directory.CreateDirectory(_root);
        var workbook = Path.Combine(_root, "repair.xlsx");
        var originalDate = new DateTime(2026, 7, 19, 20, 8, 48);
        CreateWorkbook(workbook, includeUnformattedDate: true, unformattedDate: originalDate);
        var (connector, videoPath) = await CreateConnectorWithVideoAsync(workbook, "repair.mp4");

        await connector.PushRecordAsync(CreateRecord("SF-REPAIR", videoPath));

        using var document = SpreadsheetDocument.Open(workbook, false);
        var workbookPart = document.WorkbookPart!;
        var row = GetTargetRows(workbookPart).Single(item => item.RowIndex?.Value == 3U);
        var dateCell = FindCell(row, "A")!;
        Assert.Equal(originalDate.ToOADate(), double.Parse(dateCell.CellValue!.Text, CultureInfo.InvariantCulture), 10);
        Assert.Equal(1U, dateCell.StyleIndex?.Value);
        Assert.Equal("OLD002", FindCell(row, "B")!.InlineString!.InnerText);
    }

    [Fact]
    public async Task CreatesWpsCompatibleDateStyleWhenWorkbookHasNoDateStyle()
    {
        Directory.CreateDirectory(_root);
        var workbook = Path.Combine(_root, "fallback-style.xlsx");
        CreateWorkbook(workbook, includeDateStyle: false);
        var (connector, videoPath) = await CreateConnectorWithVideoAsync(workbook, "fallback.mp4");

        await connector.PushRecordAsync(CreateRecord("SF-FALLBACK", videoPath));

        using var document = SpreadsheetDocument.Open(workbook, false);
        var workbookPart = document.WorkbookPart!;
        var dateCell = FindCell(GetTargetRows(workbookPart)[^1], "A")!;
        var cellFormat = GetCellFormat(workbookPart, dateCell);
        var numberFormat = workbookPart.WorkbookStylesPart!.Stylesheet!.NumberingFormats!
            .Elements<NumberingFormat>()
            .Single(format => format.NumberFormatId?.Value == cellFormat.NumberFormatId?.Value);
        Assert.True(cellFormat.ApplyNumberFormat?.Value);
        Assert.Equal("m\"月\"d\"日\"", numberFormat.FormatCode?.Value);
    }

    [Fact]
    public async Task LockedWorkbookIsQueuedByThrowingTypedException()
    {
        Directory.CreateDirectory(_root);
        var workbook = Path.Combine(_root, "locked.xlsx");
        CreateWorkbook(workbook);
        var video = Path.Combine(_root, "test.mp4");
        await File.WriteAllBytesAsync(video, [1]);
        var connector = new ExcelConnector(new ExcelConnectorOptions
        {
            WorkbookPath = workbook,
            BackupRoot = Path.Combine(_root, "backups")
        });
        using var lockStream = new FileStream(workbook, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        await Assert.ThrowsAsync<WorkbookLockedException>(() => connector.PushRecordAsync(new ScanRecord
        {
            TrackingNo = "SF1234567890",
            State = RecordingState.Completed,
            ScannedAt = DateTimeOffset.Now,
            VideoPath = video,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now
        }));
    }

    private async Task<(ExcelConnector Connector, string VideoPath)> CreateConnectorWithVideoAsync(
        string workbook,
        string videoName)
    {
        var video = Path.Combine(_root, videoName);
        await File.WriteAllBytesAsync(video, [1, 2, 3, 4]);
        return (new ExcelConnector(new ExcelConnectorOptions
        {
            WorkbookPath = workbook,
            BackupRoot = Path.Combine(_root, "backups")
        }), video);
    }

    private static ScanRecord CreateRecord(string trackingNo, string videoPath) => new()
    {
        TrackingNo = trackingNo,
        State = RecordingState.Completed,
        ScannedAt = new DateTimeOffset(2026, 7, 19, 21, 15, 30, TimeSpan.FromHours(8)),
        VideoPath = videoPath,
        CreatedAt = DateTimeOffset.Now,
        UpdatedAt = DateTimeOffset.Now
    };

    private static void CreateWorkbook(
        string path,
        bool includeDateStyle = true,
        bool includeUnformattedDate = false,
        DateTime? unformattedDate = null)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var styles = workbookPart.AddNewPart<WorkbookStylesPart>();
        var cellFormats = new CellFormats(new CellFormat()) { Count = 1 };
        if (includeDateStyle)
        {
            cellFormats.Append(new CellFormat
            {
                NumberFormatId = 58U,
                ApplyNumberFormat = true,
                ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Center }
            });
            cellFormats.Count = 2;
        }
        styles.Stylesheet = new Stylesheet(
            new Fonts(new Font()) { Count = 1 },
            new Fills(new Fill()) { Count = 1 },
            new Borders(new Border()) { Count = 1 },
            cellFormats);
        styles.Stylesheet.Save();
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        var targetPart = AddSheet(workbookPart, sheets, "退货扫码单号", 1, [
            ["扫码日期", "扫码枪扫描快递单号", "是否与平台关联", "金额", "备注", "图片凭证"],
            ["", "OLD001", "单号已关联", "", "", ""],
            ["", "OLD002", "单号已关联", "", "", ""]
        ]);
        SetNumericDate(targetPart, 2, new DateTime(2026, 7, 18), includeDateStyle ? 1U : null);
        if (includeUnformattedDate)
        {
            SetNumericDate(targetPart, 3, unformattedDate ?? new DateTime(2026, 7, 19), null);
        }
        AddSheet(workbookPart, sheets, "店口五金-拼多多", 2, [["退款申请时间", "退货运单号"]]);
        AddSheet(workbookPart, sheets, "店口五金-淘宝", 3, [["退款申请时间", "单号"]]);
        AddSheet(workbookPart, sheets, "店口五金-京东", 4, [["退款申请时间", "退货运单号"]]);
        workbookPart.Workbook.Save();
    }

    private static WorksheetPart AddSheet(WorkbookPart workbookPart, Sheets sheets, string name, uint id, string[][] values)
    {
        var part = workbookPart.AddNewPart<WorksheetPart>();
        var data = new SheetData();
        for (var rowIndex = 0; rowIndex < values.Length; rowIndex++)
        {
            var row = new Row { RowIndex = (uint)(rowIndex + 1) };
            for (var columnIndex = 0; columnIndex < values[rowIndex].Length; columnIndex++)
            {
                if (string.IsNullOrEmpty(values[rowIndex][columnIndex]))
                {
                    continue;
                }
                var column = (char)('A' + columnIndex);
                row.Append(new Cell
                {
                    CellReference = $"{column}{rowIndex + 1}",
                    DataType = CellValues.InlineString,
                    InlineString = new InlineString(new Text(values[rowIndex][columnIndex]))
                });
            }
            data.Append(row);
        }
        part.Worksheet = new Worksheet(data);
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(part),
            SheetId = id,
            Name = name
        });
        return part;
    }

    private static void SetNumericDate(WorksheetPart part, uint rowIndex, DateTime value, uint? styleIndex)
    {
        var data = part.Worksheet!.GetFirstChild<SheetData>()!;
        var row = data.Elements<Row>().Single(item => item.RowIndex?.Value == rowIndex);
        var cell = new Cell
        {
            CellReference = $"A{rowIndex}",
            DataType = CellValues.Number,
            CellValue = new CellValue(value.ToOADate().ToString(CultureInfo.InvariantCulture)),
            StyleIndex = styleIndex
        };
        row.PrependChild(cell);
        part.Worksheet.Save();
    }

    private static List<Row> GetTargetRows(WorkbookPart workbookPart)
    {
        var sheet = workbookPart.Workbook!.Sheets!.Elements<Sheet>().Single(item => item.Name == "退货扫码单号");
        var part = (WorksheetPart)workbookPart.GetPartById(sheet.Id!);
        return part.Worksheet!.GetFirstChild<SheetData>()!.Elements<Row>().ToList();
    }

    private static CellFormat GetCellFormat(WorkbookPart workbookPart, Cell cell) =>
        workbookPart.WorkbookStylesPart!.Stylesheet!.CellFormats!.Elements<CellFormat>()
            .ElementAt((int)(cell.StyleIndex?.Value ?? 0U));

    private static Cell? FindCell(Row row, string column) => row.Elements<Cell>().FirstOrDefault(cell =>
        string.Equals(
            new string(cell.CellReference!.Value!.TakeWhile(char.IsLetter).ToArray()),
            column,
            StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
        GC.SuppressFinalize(this);
    }
}
