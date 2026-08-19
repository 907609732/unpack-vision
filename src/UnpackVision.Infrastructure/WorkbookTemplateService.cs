using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

public sealed class WorkbookTemplateService : IWorkbookTemplateService
{
    public const string SyncSheetName = "__UnpackVisionSync";
    private static readonly string[] Headers =
        ["扫码日期", "快递单号", "关联结果", "金额", "备注", "图片凭证"];

    public Task CreateAsync(
        string path,
        string worksheetName = "退货扫码单号",
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(worksheetName);
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(fullPath), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("新表格必须使用 .xlsx 格式", nameof(path));
        }
        if (File.Exists(fullPath))
        {
            throw new IOException($"文件已存在，不会覆盖：{fullPath}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = Path.Combine(
            Path.GetDirectoryName(fullPath)!,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var document = SpreadsheetDocument.Create(
                       temporary,
                       SpreadsheetDocumentType.Workbook,
                       true))
            {
                var workbookPart = document.AddWorkbookPart();
                workbookPart.Workbook = new Workbook();
                var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
                stylesPart.Stylesheet = CreateStylesheet();
                stylesPart.Stylesheet.Save();
                var sheets = workbookPart.Workbook.AppendChild(new Sheets());

                var mainPart = workbookPart.AddNewPart<WorksheetPart>();
                mainPart.Worksheet = CreateMainWorksheet();
                sheets.Append(new Sheet
                {
                    Id = workbookPart.GetIdOfPart(mainPart),
                    SheetId = 1U,
                    Name = worksheetName
                });

                var syncPart = workbookPart.AddNewPart<WorksheetPart>();
                syncPart.Worksheet = new Worksheet(new SheetData(
                    new Row(
                        InlineCell("A1", "RecordId", 1U),
                        InlineCell("B1", "TargetRow", 1U),
                        InlineCell("C1", "SyncedAt", 1U))
                    { RowIndex = 1U }));
                sheets.Append(new Sheet
                {
                    Id = workbookPart.GetIdOfPart(syncPart),
                    SheetId = 2U,
                    Name = SyncSheetName,
                    State = SheetStateValues.VeryHidden
                });
                workbookPart.Workbook.CalculationProperties = new CalculationProperties
                {
                    CalculationMode = CalculateModeValues.Auto,
                    FullCalculationOnLoad = true
                };
                workbookPart.Workbook.Save();
            }
            File.Move(temporary, fullPath, false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
        return Task.CompletedTask;
    }

    public Task<WorkbookValidationResult> ValidateAsync(
        string path,
        string worksheetName = "退货扫码单号",
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Task.FromResult(new WorkbookValidationResult(false, "Excel 工作簿不存在"));
        }
        try
        {
            // An exclusive read/write probe verifies permissions and detects an
            // open Excel/WPS session without changing workbook contents.
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
            }
            using var document = SpreadsheetDocument.Open(path, false);
            var workbook = document.WorkbookPart?.Workbook;
            var sheet = workbook?.Sheets?.Elements<Sheet>()
                .FirstOrDefault(item => string.Equals(item.Name?.Value, worksheetName, StringComparison.Ordinal));
            if (sheet is null)
            {
                return Task.FromResult(new WorkbookValidationResult(
                    false,
                    $"找不到工作表：{worksheetName}",
                    worksheetName));
            }
            var part = (WorksheetPart)document.WorkbookPart!.GetPartById(sheet.Id!);
            var firstRow = part.Worksheet?.GetFirstChild<SheetData>()?.Elements<Row>().FirstOrDefault();
            var actualHeaders = firstRow?.Elements<Cell>().Select(ReadInlineText).ToArray() ?? [];
            if (actualHeaders.Length < Headers.Length ||
                !Headers.SequenceEqual(actualHeaders.Take(Headers.Length), StringComparer.Ordinal))
            {
                return Task.FromResult(new WorkbookValidationResult(
                    false,
                    "目标工作表的六列表头与标准模板不一致",
                    worksheetName));
            }
            return Task.FromResult(new WorkbookValidationResult(true, "Excel 连接正常", worksheetName));
        }
        catch (IOException ex)
        {
            return Task.FromResult(new WorkbookValidationResult(false, $"工作簿正在被占用：{ex.Message}"));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult(new WorkbookValidationResult(false, $"工作簿不可写：{ex.Message}"));
        }
        catch (OpenXmlPackageException ex)
        {
            return Task.FromResult(new WorkbookValidationResult(false, $"工作簿格式无效：{ex.Message}"));
        }
    }

    private static Worksheet CreateMainWorksheet()
    {
        var header = new Row { RowIndex = 1U, Height = 24D, CustomHeight = true };
        for (var index = 0; index < Headers.Length; index++)
        {
            header.Append(InlineCell($"{(char)('A' + index)}1", Headers[index], 1U));
        }
        return new Worksheet(
            new SheetViews(new SheetView(
                new Pane
                {
                    VerticalSplit = 1D,
                    TopLeftCell = "A2",
                    ActivePane = PaneValues.BottomLeft,
                    State = PaneStateValues.Frozen
                })
            { WorkbookViewId = 0U }),
            new Columns(
                new Column { Min = 1U, Max = 1U, Width = 14D, CustomWidth = true },
                new Column { Min = 2U, Max = 2U, Width = 24D, CustomWidth = true },
                new Column { Min = 3U, Max = 3U, Width = 18D, CustomWidth = true },
                new Column { Min = 4U, Max = 4U, Width = 12D, CustomWidth = true },
                new Column { Min = 5U, Max = 5U, Width = 34D, CustomWidth = true },
                new Column { Min = 6U, Max = 6U, Width = 20D, CustomWidth = true }),
            new SheetData(header),
            new AutoFilter { Reference = "A1:F1" });
    }

    private static Stylesheet CreateStylesheet()
    {
        var numberingFormats = new NumberingFormats(
            new NumberingFormat
            {
                NumberFormatId = 164U,
                FormatCode = "m\"月\"d\"日\""
            })
        { Count = 1U };
        var fonts = new Fonts(
            new Font(),
            new Font(new Bold(), new Color { Rgb = "FFFFFFFF" }))
        { Count = 2U };
        var fills = new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
            new Fill(new PatternFill(
                new ForegroundColor { Rgb = "FF2F7CF6" },
                new BackgroundColor { Indexed = 64U })
            { PatternType = PatternValues.Solid }))
        { Count = 3U };
        var borders = new Borders(new Border()) { Count = 1U };
        var formats = new CellFormats(
            new CellFormat(),
            new CellFormat
            {
                FontId = 1U,
                FillId = 2U,
                Alignment = new Alignment
                {
                    Horizontal = HorizontalAlignmentValues.Center,
                    Vertical = VerticalAlignmentValues.Center
                },
                ApplyAlignment = true
            },
            new CellFormat { NumberFormatId = 164U, ApplyNumberFormat = true },
            new CellFormat { NumberFormatId = 49U, ApplyNumberFormat = true })
        { Count = 4U };
        return new Stylesheet(numberingFormats, fonts, fills, borders, formats);
    }

    private static Cell InlineCell(string reference, string value, uint style) =>
        new()
        {
            CellReference = reference,
            DataType = CellValues.InlineString,
            StyleIndex = style,
            InlineString = new InlineString(new Text(value))
        };

    private static string ReadInlineText(Cell cell) =>
        cell.InlineString?.Text?.Text ??
        cell.InlineString?.InnerText ??
        cell.CellValue?.Text ??
        string.Empty;
}
