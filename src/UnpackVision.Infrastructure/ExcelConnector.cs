using System.Globalization;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

public sealed class WorkbookLockedException(string message, Exception? innerException = null)
    : IOException(message, innerException);

public sealed class ExcelConnector : ISyncConnector
{
    private const string SyncSheetName = "__UnpackVisionSync";
    private const string AnnotationPrefix = "【电商拆包智能录像】";
    private static readonly string[] OwnedAnnotationPrefixes = [AnnotationPrefix, "【拆包智录】"];
    private const string DefaultDateFormatCode = "m\"月\"d\"日\"";
    private static readonly double MinimumSupportedOaDate = new DateTime(2000, 1, 1).ToOADate();
    private static readonly double MaximumSupportedOaDate = new DateTime(2100, 12, 31, 23, 59, 59).ToOADate();
    private readonly ExcelConnectorOptions _options;

    public ExcelConnector(ExcelConnectorOptions options) => _options = options;

    public string Id => _options.ConnectorId;

    public IReadOnlyList<string> ValidateConfiguration()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(_options.WorkbookPath))
        {
            errors.Add("尚未配置 Excel 工作簿路径");
        }
        else if (!File.Exists(_options.WorkbookPath))
        {
            errors.Add($"Excel 工作簿不存在：{_options.WorkbookPath}");
        }

        if (string.IsNullOrWhiteSpace(_options.WorksheetName))
        {
            errors.Add("尚未配置目标工作表名称");
        }
        return errors;
    }

    public Task<ConnectorHealth> GetHealthAsync(CancellationToken cancellationToken = default) =>
        TestConnectionAsync(cancellationToken);

    public Task<ConnectorHealth> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var errors = ValidateConfiguration();
        if (errors.Count > 0)
        {
            return Task.FromResult(new ConnectorHealth(false, string.Join("；", errors)));
        }

        try
        {
            using var document = SpreadsheetDocument.Open(_options.WorkbookPath, false);
            var sheet = FindSheet(document, _options.WorksheetName);
            return Task.FromResult(sheet is null
                ? new ConnectorHealth(false, $"找不到工作表：{_options.WorksheetName}")
                : new ConnectorHealth(true, "Excel 连接正常"));
        }
        catch (IOException ex)
        {
            return Task.FromResult(new ConnectorHealth(false, $"工作簿正在被占用：{ex.Message}"));
        }
        catch (OpenXmlPackageException ex)
        {
            return Task.FromResult(new ConnectorHealth(false, $"工作簿格式无效：{ex.Message}"));
        }
    }

    public async Task<SyncPushResult> PushRecordAsync(
        ScanRecord record,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var isScanCollection = record.Workflow == WorkflowMode.ScanCollection &&
                               record.State == RecordingState.Collected;
        if (record.State is not (RecordingState.Completed or RecordingState.Imported) && !isScanCollection)
        {
            throw new InvalidOperationException("只有已完成录像、已确认导入录像或扫码收集记录才能同步到 Excel");
        }
        if (!isScanCollection &&
            (string.IsNullOrWhiteSpace(record.VideoPath) || !File.Exists(record.VideoPath)))
        {
            throw new FileNotFoundException("录像文件不存在，已阻止写入 Excel", record.VideoPath);
        }

        var errors = ValidateConfiguration();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join("；", errors));
        }

        var workbookPath = Path.GetFullPath(_options.WorkbookPath);
        var workbookDirectory = Path.GetDirectoryName(workbookPath)!;
        var tempPath = Path.Combine(
            workbookDirectory,
            $".{Path.GetFileName(workbookPath)}.{Guid.NewGuid():N}.tmp.xlsx");

        string backupPath;
        try
        {
            backupPath = CreateExclusiveCopies(workbookPath, tempPath, cancellationToken);
        }
        catch (IOException ex)
        {
            throw new WorkbookLockedException("Excel 工作簿正在使用中，将在关闭后自动重试", ex);
        }

        try
        {
            var rowIndex = AppendRecord(tempPath, record);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Replace(tempPath, workbookPath, null, true);
            }
            catch (IOException ex)
            {
                throw new WorkbookLockedException("Excel 工作簿在写入期间被重新打开，将稍后重试", ex);
            }

            CleanupOldBackups(_options.BackupRoot, TimeSpan.FromDays(30));
            return new SyncPushResult(rowIndex.ToString(CultureInfo.InvariantCulture), $"已写入第 {rowIndex} 行；备份：{backupPath}");
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private string CreateExclusiveCopies(string workbookPath, string tempPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.BackupRoot);
        var backupName = $"{Path.GetFileNameWithoutExtension(workbookPath)}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.xlsx";
        var backupPath = Path.Combine(_options.BackupRoot, backupName);

        using var source = new FileStream(workbookPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using (var backup = new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            source.CopyTo(backup);
            backup.Flush(true);
        }
        cancellationToken.ThrowIfCancellationRequested();
        source.Position = 0;
        using (var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            source.CopyTo(temp);
            temp.Flush(true);
        }
        return backupPath;
    }

    private uint AppendRecord(string tempPath, ScanRecord record)
    {
        using var document = SpreadsheetDocument.Open(tempPath, true);
        var workbookPart = document.WorkbookPart ?? throw new InvalidDataException("工作簿缺少 WorkbookPart");
        var workbook = workbookPart.Workbook ?? throw new InvalidDataException("工作簿缺少 Workbook");
        var targetSheet = FindSheet(document, _options.WorksheetName)
            ?? throw new InvalidOperationException($"找不到工作表：{_options.WorksheetName}");
        var targetPart = (WorksheetPart)workbookPart.GetPartById(targetSheet.Id!);
        var targetWorksheet = targetPart.Worksheet ?? throw new InvalidDataException("目标工作表内容为空");
        var syncPart = GetOrCreateSyncSheet(workbookPart);
        var syncWorksheet = syncPart.Worksheet ?? throw new InvalidDataException("同步标记工作表内容为空");
        var sheetData = targetWorksheet.GetFirstChild<SheetData>()
            ?? targetWorksheet.AppendChild(new SheetData());
        var lastDataRow = FindLastDataRow(workbookPart, sheetData);
        var dateStyle = ResolveDateStyle(workbookPart, sheetData);
        RepairDateColumnStyles(workbookPart, sheetData, dateStyle);

        if (ContainsSyncMarker(syncPart, record.Id))
        {
            var markedRow = FindMarkedRow(syncPart, record.Id);
            UpdateAnnotation(workbookPart, sheetData, markedRow, record);
            targetWorksheet.Save();
            syncWorksheet.Save();
            workbook.Save();
            return markedRow;
        }

        var rowIndex = Math.Max(1, lastDataRow?.RowIndex?.Value ?? 1) + 1;
        var row = new Row { RowIndex = rowIndex };
        if (lastDataRow is not null)
        {
            row.CustomHeight = lastDataRow.CustomHeight;
            row.Height = lastDataRow.Height;
        }

        row.Append(
            CreateDateCell("A", rowIndex, record.ScannedAt, dateStyle),
            CreateTextCell("B", rowIndex, record.TrackingNo, StyleOf(lastDataRow, "B")),
            CreateFormulaCell("C", rowIndex, AssociationFormula(rowIndex), StyleOf(lastDataRow, "C")),
            CreateBlankCell("D", rowIndex, StyleOf(lastDataRow, "D")),
            string.IsNullOrWhiteSpace(BuildAnnotation(record))
                ? CreateBlankCell("E", rowIndex, StyleOf(lastDataRow, "E"))
                : CreateTextCell("E", rowIndex, BuildAnnotation(record), StyleOf(lastDataRow, "E")),
            CreateBlankCell("F", rowIndex, StyleOf(lastDataRow, "F")));
        sheetData.Append(row);

        AppendSyncMarker(syncPart, record, rowIndex);
        var calculationProperties = workbook.CalculationProperties
            ?? workbook.AppendChild(new CalculationProperties());
        calculationProperties.CalculationMode = CalculateModeValues.Auto;
        calculationProperties.FullCalculationOnLoad = true;
        calculationProperties.ForceFullCalculation = true;
        calculationProperties.CalculationId = 0U;
        targetWorksheet.Save();
        syncWorksheet.Save();
        workbook.Save();
        return rowIndex;
    }

    private static UInt32Value ResolveDateStyle(WorkbookPart workbookPart, SheetData sheetData)
    {
        foreach (var row in sheetData.Elements<Row>().OrderByDescending(item => item.RowIndex?.Value ?? 0U))
        {
            var cell = FindCell(row, "A");
            if (!TryGetSupportedOaDate(cell, out _) || cell?.StyleIndex is null)
            {
                continue;
            }

            if (IsDateStyle(workbookPart, cell.StyleIndex.Value))
            {
                return new UInt32Value(cell.StyleIndex.Value);
            }
        }

        var baseStyle = sheetData.Elements<Row>()
            .OrderByDescending(item => item.RowIndex?.Value ?? 0U)
            .Select(item => FindCell(item, "A")?.StyleIndex?.Value)
            .FirstOrDefault(value => value.HasValue);
        return EnsureDefaultDateStyle(workbookPart, baseStyle);
    }

    private static void RepairDateColumnStyles(
        WorkbookPart workbookPart,
        SheetData sheetData,
        UInt32Value dateStyle)
    {
        foreach (var row in sheetData.Elements<Row>())
        {
            var cell = FindCell(row, "A");
            if (!TryGetSupportedOaDate(cell, out _) ||
                (cell?.StyleIndex is not null && IsDateStyle(workbookPart, cell.StyleIndex.Value)))
            {
                continue;
            }

            cell!.StyleIndex = new UInt32Value(dateStyle.Value);
        }
    }

    private static bool TryGetSupportedOaDate(Cell? cell, out double value)
    {
        value = 0;
        if (cell is null)
        {
            return false;
        }

        var dataType = cell.DataType?.Value;
        if (dataType == CellValues.SharedString ||
            dataType == CellValues.InlineString ||
            dataType == CellValues.String)
        {
            return false;
        }

        return double.TryParse(cell.CellValue?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
               value >= MinimumSupportedOaDate &&
               value <= MaximumSupportedOaDate;
    }

    private static bool IsDateStyle(WorkbookPart workbookPart, uint styleIndex)
    {
        var stylesheet = workbookPart.WorkbookStylesPart?.Stylesheet;
        var cellFormat = stylesheet?.CellFormats?.Elements<CellFormat>().ElementAtOrDefault((int)styleIndex);
        var numberFormatId = cellFormat?.NumberFormatId?.Value ?? 0U;
        if (IsBuiltInDateFormat(numberFormatId))
        {
            return true;
        }

        var formatCode = stylesheet?.NumberingFormats?.Elements<NumberingFormat>()
            .FirstOrDefault(format => format.NumberFormatId?.Value == numberFormatId)
            ?.FormatCode?.Value;
        return LooksLikeDateFormat(formatCode);
    }

    private static bool IsBuiltInDateFormat(uint numberFormatId) =>
        numberFormatId is >= 14 and <= 22 or >= 27 and <= 36 or >= 45 and <= 47 or >= 50 and <= 58;

    private static bool LooksLikeDateFormat(string? formatCode)
    {
        if (string.IsNullOrWhiteSpace(formatCode))
        {
            return false;
        }

        var normalized = Regex.Replace(formatCode, "\"[^\"]*\"|\\\\.|\\[[^\\]]*\\]", string.Empty)
            .ToLowerInvariant();
        return normalized.IndexOfAny(['y', 'm', 'd', 'h', 's']) >= 0;
    }

    private static UInt32Value EnsureDefaultDateStyle(WorkbookPart workbookPart, uint? baseStyleIndex)
    {
        var stylesPart = workbookPart.WorkbookStylesPart ?? workbookPart.AddNewPart<WorkbookStylesPart>();
        var stylesheet = stylesPart.Stylesheet ??= new Stylesheet();
        var numberingFormats = stylesheet.NumberingFormats;
        if (numberingFormats is null)
        {
            numberingFormats = new NumberingFormats();
            stylesheet.InsertAt(numberingFormats, 0);
        }

        var numberFormat = numberingFormats.Elements<NumberingFormat>()
            .FirstOrDefault(format => string.Equals(
                format.FormatCode?.Value,
                DefaultDateFormatCode,
                StringComparison.Ordinal));
        if (numberFormat is null)
        {
            var nextId = numberingFormats.Elements<NumberingFormat>()
                .Select(format => format.NumberFormatId?.Value ?? 163U)
                .DefaultIfEmpty(163U)
                .Max() + 1U;
            nextId = Math.Max(164U, nextId);
            numberFormat = new NumberingFormat
            {
                NumberFormatId = nextId,
                FormatCode = DefaultDateFormatCode
            };
            numberingFormats.Append(numberFormat);
            numberingFormats.Count = (uint)numberingFormats.ChildElements.Count;
        }

        var cellFormats = stylesheet.CellFormats;
        if (cellFormats is null)
        {
            cellFormats = new CellFormats(new CellFormat());
            stylesheet.Append(cellFormats);
        }

        var formats = cellFormats.Elements<CellFormat>().ToList();
        var existingIndex = formats.FindIndex(format =>
            format.NumberFormatId?.Value == numberFormat.NumberFormatId?.Value &&
            format.ApplyNumberFormat?.Value == true);
        if (existingIndex >= 0)
        {
            stylesPart.Stylesheet.Save();
            return new UInt32Value((uint)existingIndex);
        }

        var baseFormat = baseStyleIndex.HasValue && baseStyleIndex.Value < formats.Count
            ? formats[(int)baseStyleIndex.Value]
            : formats.FirstOrDefault() ?? new CellFormat();
        var dateFormat = (CellFormat)baseFormat.CloneNode(true);
        dateFormat.NumberFormatId = numberFormat.NumberFormatId?.Value ?? 164U;
        dateFormat.ApplyNumberFormat = true;
        cellFormats.Append(dateFormat);
        cellFormats.Count = (uint)cellFormats.ChildElements.Count;
        stylesPart.Stylesheet.Save();
        return new UInt32Value((uint)cellFormats.ChildElements.Count - 1U);
    }

    private static Sheet? FindSheet(SpreadsheetDocument document, string name)
    {
        var workbook = document.WorkbookPart?.Workbook;
        return workbook?.Sheets?.Elements<Sheet>()
            .FirstOrDefault(sheet => string.Equals(sheet.Name?.Value, name, StringComparison.Ordinal));
    }

    private static WorksheetPart GetOrCreateSyncSheet(WorkbookPart workbookPart)
    {
        var workbook = workbookPart.Workbook ?? throw new InvalidDataException("工作簿缺少 Workbook");
        var existing = workbook.Sheets?.Elements<Sheet>()
            .FirstOrDefault(sheet => string.Equals(sheet.Name?.Value, SyncSheetName, StringComparison.Ordinal));
        if (existing is not null)
        {
            return (WorksheetPart)workbookPart.GetPartById(existing.Id!);
        }

        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        worksheetPart.Worksheet = new Worksheet(new SheetData(
            new Row(
                CreateTextCell("A", 1, "RecordId", null),
                CreateTextCell("B", 1, "TargetRow", null),
                CreateTextCell("C", 1, "SyncedAt", null))));
        var sheets = workbook.Sheets ?? workbook.AppendChild(new Sheets());
        var sheetId = sheets.Elements<Sheet>().Select(sheet => sheet.SheetId?.Value ?? 0U).DefaultIfEmpty().Max() + 1;
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = sheetId,
            Name = SyncSheetName,
            State = SheetStateValues.VeryHidden
        });
        return worksheetPart;
    }

    private static Row? FindLastDataRow(WorkbookPart workbookPart, SheetData sheetData)
    {
        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;
        return sheetData.Elements<Row>()
            .Where(row => !string.IsNullOrWhiteSpace(GetCellText(FindCell(row, "B"), sharedStrings)))
            .OrderBy(row => row.RowIndex?.Value ?? 0U)
            .LastOrDefault();
    }

    private static bool ContainsSyncMarker(WorksheetPart syncPart, Guid recordId)
    {
        var worksheet = syncPart.Worksheet ?? throw new InvalidDataException("同步标记工作表内容为空");
        return worksheet.GetFirstChild<SheetData>()?.Elements<Row>()
            .Skip(1)
            .Any(row => string.Equals(GetInlineText(FindCell(row, "A")), recordId.ToString("D"), StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static uint FindMarkedRow(WorksheetPart syncPart, Guid recordId)
    {
        var worksheet = syncPart.Worksheet ?? throw new InvalidDataException("同步标记工作表内容为空");
        var row = worksheet.GetFirstChild<SheetData>()?.Elements<Row>()
            .Skip(1)
            .FirstOrDefault(item => string.Equals(GetInlineText(FindCell(item, "A")), recordId.ToString("D"), StringComparison.OrdinalIgnoreCase));
        var value = GetInlineText(row is null ? null : FindCell(row, "B"));
        return uint.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0U;
    }

    private static void AppendSyncMarker(WorksheetPart syncPart, ScanRecord record, uint targetRow)
    {
        var worksheet = syncPart.Worksheet ?? throw new InvalidDataException("同步标记工作表内容为空");
        var data = worksheet.GetFirstChild<SheetData>() ?? worksheet.AppendChild(new SheetData());
        var index = (data.Elements<Row>().Select(row => row.RowIndex?.Value ?? 0U).DefaultIfEmpty().Max()) + 1;
        data.Append(new Row(
            CreateTextCell("A", index, record.Id.ToString("D"), null),
            CreateTextCell("B", index, targetRow.ToString(CultureInfo.InvariantCulture), null),
            CreateTextCell("C", index, DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture), null))
        { RowIndex = index });
    }

    private static void UpdateAnnotation(WorkbookPart workbookPart, SheetData sheetData, uint rowIndex, ScanRecord record)
    {
        if (rowIndex == 0)
        {
            throw new InvalidDataException($"记录 {record.Id} 的 Excel 同步标记缺少目标行");
        }
        var row = sheetData.Elements<Row>().FirstOrDefault(item => item.RowIndex?.Value == rowIndex)
            ?? throw new InvalidDataException($"Excel 第 {rowIndex} 行不存在，已停止更新备注");
        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;
        var tracking = GetCellText(FindCell(row, "B"), sharedStrings);
        if (!string.Equals(tracking, record.TrackingNo, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Excel 第 {rowIndex} 行单号已变化，已停止覆盖备注");
        }

        var cell = FindCell(row, "E");
        var style = cell?.StyleIndex;
        var merged = MergeAnnotation(GetCellText(cell, sharedStrings), BuildAnnotation(record));
        if (cell is not null)
        {
            cell.Remove();
        }
        var replacement = string.IsNullOrWhiteSpace(merged)
            ? CreateBlankCell("E", rowIndex, style)
            : CreateTextCell("E", rowIndex, merged, style);
        var next = row.Elements<Cell>().FirstOrDefault(item =>
            string.Compare(GetColumnName(item.CellReference?.Value), "E", StringComparison.OrdinalIgnoreCase) > 0);
        if (next is null)
        {
            row.Append(replacement);
        }
        else
        {
            row.InsertBefore(replacement, next);
        }
    }

    internal static string BuildAnnotation(ScanRecord record)
    {
        var tags = record.Tags.Where(item => item.IsActive).OrderBy(item => item.TaggedAt)
            .Select(item => item.TagName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var parts = new List<string>();
        if (tags.Length > 0)
        {
            parts.Add($"异常：{string.Join('、', tags)}");
        }
        if (!string.IsNullOrWhiteSpace(record.Note))
        {
            parts.Add($"备注：{record.Note.Trim().Replace("\r", " ").Replace("\n", " ")}");
        }
        return AnnotationPrefix + string.Join("；", parts);
    }

    internal static string MergeAnnotation(string? existing, string annotation)
    {
        var lines = (existing ?? string.Empty)
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line =>
            {
                var marker = OwnedAnnotationPrefixes
                    .Select(prefix => line.IndexOf(prefix, StringComparison.Ordinal))
                    .Where(index => index >= 0)
                    .DefaultIfEmpty(-1)
                    .Min();
                return marker < 0 ? line : line[..marker].TrimEnd(' ', '；', ';');
            })
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }
        if (!string.IsNullOrWhiteSpace(annotation))
        {
            lines.Add(annotation);
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static Cell CreateDateCell(string column, uint row, DateTimeOffset value, UInt32Value? style) => new()
    {
        CellReference = $"{column}{row}",
        DataType = CellValues.Number,
        CellValue = new CellValue(value.LocalDateTime.ToOADate().ToString(CultureInfo.InvariantCulture)),
        StyleIndex = style
    };

    private static Cell CreateTextCell(string column, uint row, string value, UInt32Value? style) => new()
    {
        CellReference = $"{column}{row}",
        DataType = CellValues.InlineString,
        InlineString = new InlineString(new Text(value) { Space = SpaceProcessingModeValues.Preserve }),
        StyleIndex = style
    };

    private static Cell CreateFormulaCell(string column, uint row, string formula, UInt32Value? style) => new()
    {
        CellReference = $"{column}{row}",
        CellFormula = new CellFormula(formula),
        StyleIndex = style
    };

    private static Cell CreateBlankCell(string column, uint row, UInt32Value? style) => new()
    {
        CellReference = $"{column}{row}",
        StyleIndex = style
    };

    private static string AssociationFormula(uint row) =>
        $"IF(COUNTIF('店口五金-拼多多'!B:B,B{row})+COUNTIF('店口五金-淘宝'!B:B,B{row})+COUNTIF('店口五金-京东'!B:B,B{row})>0,\"单号已关联\",\"无头件\")";

    private static UInt32Value? StyleOf(Row? row, string column) => FindCell(row, column)?.StyleIndex;

    private static Cell? FindCell(Row? row, string column) => row?.Elements<Cell>().FirstOrDefault(cell =>
        string.Equals(GetColumnName(cell.CellReference?.Value), column, StringComparison.OrdinalIgnoreCase));

    private static string GetColumnName(string? reference) =>
        string.IsNullOrWhiteSpace(reference)
            ? string.Empty
            : new string(reference.TakeWhile(char.IsLetter).ToArray());

    private static string? GetCellText(Cell? cell, SharedStringTable? sharedStrings)
    {
        if (cell is null)
        {
            return null;
        }
        if (cell.DataType?.Value == CellValues.SharedString &&
            int.TryParse(cell.CellValue?.Text, out var index))
        {
            return sharedStrings?.Elements<SharedStringItem>().ElementAtOrDefault(index)?.InnerText;
        }
        if (cell.DataType?.Value == CellValues.InlineString)
        {
            return cell.InlineString?.InnerText;
        }
        return cell.CellValue?.Text;
    }

    private static string? GetInlineText(Cell? cell) => cell?.InlineString?.InnerText ?? cell?.CellValue?.Text;

    private static void CleanupOldBackups(string root, TimeSpan retention)
    {
        if (!Directory.Exists(root))
        {
            return;
        }
        var threshold = DateTime.UtcNow - retention;
        foreach (var file in Directory.EnumerateFiles(root, "*.xlsx", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < threshold)
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
