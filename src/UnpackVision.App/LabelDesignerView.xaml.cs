using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Win32;
using UnpackVision.Infrastructure;
using UnpackVision.Core;

namespace UnpackVision.App;

public partial class LabelDesignerView : UserControl
{
    private sealed record IssueCommandChoice(string Name, string Value);
    private const double EditPixelsPerMm = 10;
    private const double PrintPixelsPerMm = 96d / 25.4d;
    private readonly LabelTemplateStore _store = new();
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private LabelTemplateDocument _document = new();
    private LabelElementModel? _selected;
    private LabelElementModel? _clipboard;
    private List<Dictionary<string, string>> _dataRows = [];
    private int _dataRowIndex;
    private bool _loading = true;
    private bool _initialized;
    private Point _dragStart;
    private double _dragX;
    private double _dragY;

    public LabelDesignerView()
    {
        InitializeComponent();
        Loaded += (_, _) => InitializeDesigner();
        ConfigureIssueTags(IssueTagDefaults.Create());
    }

    public void ConfigureIssueTags(IReadOnlyList<IssueTagDefinition> definitions)
    {
        var choices = definitions.Where(tag => tag.Enabled).OrderBy(tag => tag.SortOrder)
            .Select(tag => new IssueCommandChoice(tag.Name, tag.BarcodeValue))
            .Append(new IssueCommandChoice("撤销上一个标签", IssueTagDefaults.UndoBarcode))
            .ToArray();
        IssueCommandSelector.ItemsSource = choices;
        IssueCommandSelector.SelectedIndex = choices.Length == 0 ? -1 : 0;
    }

    private void InitializeDesigner()
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;
        RefreshTemplateList();
        LoadDocument(new LabelTemplateDocument());
    }

    private void LoadDocument(LabelTemplateDocument document)
    {
        _loading = true;
        _document = document;
        _selected = null;
        TemplateNameInput.Text = document.Name;
        WidthInput.Text = FormatNumber(document.WidthMm);
        HeightInput.Text = FormatNumber(document.HeightMm);
        OffsetXInput.Text = FormatNumber(document.OffsetXmm);
        OffsetYInput.Text = FormatNumber(document.OffsetYmm);
        SelectPreset(document.WidthMm, document.HeightMm);
        _undo.Clear();
        _redo.Clear();
        _loading = false;
        RenderCanvas();
        LoadSelectedProperties();
    }

    private void RenderCanvas()
    {
        var zoom = ZoomSlider.Value <= 0 ? 1 : ZoomSlider.Value;
        DesignCanvas.Width = _document.WidthMm * EditPixelsPerMm * zoom;
        DesignCanvas.Height = _document.HeightMm * EditPixelsPerMm * zoom;
        DesignCanvas.Children.Clear();
        foreach (var model in _document.Elements)
        {
            var wrapper = CreateDesignerWrapper(model, zoom);
            Canvas.SetLeft(wrapper, model.X * EditPixelsPerMm * zoom);
            Canvas.SetTop(wrapper, model.Y * EditPixelsPerMm * zoom);
            DesignCanvas.Children.Add(wrapper);
        }
        if (ZoomText is not null)
        {
            ZoomText.Text = $"{zoom:P0}";
        }
    }

    private FrameworkElement CreateDesignerWrapper(LabelElementModel model, double zoom)
    {
        var scale = EditPixelsPerMm * zoom;
        var grid = new Grid
        {
            Width = Math.Max(8, model.Width * scale),
            Height = Math.Max(8, model.Height * scale),
            Tag = model,
            Background = Brushes.Transparent,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(model.Rotation)
        };
        var content = CreateElementVisual(model, scale, CurrentDataRow(), true);
        grid.Children.Add(content);
        var selection = new Border
        {
            BorderBrush = model == _selected ? new SolidColorBrush(Color.FromRgb(10, 122, 255)) : Brushes.Transparent,
            BorderThickness = new Thickness(model == _selected ? 1.5 : 0),
            IsHitTestVisible = false
        };
        grid.Children.Add(selection);
        if (model == _selected)
        {
            var thumb = new Thumb
            {
                Width = 12,
                Height = 12,
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(10, 122, 255)),
                BorderThickness = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Cursor = Cursors.SizeNWSE
            };
            thumb.DragStarted += (_, _) => PushUndo();
            thumb.DragDelta += (_, args) => ResizeSelected(args.HorizontalChange / scale, args.VerticalChange / scale);
            grid.Children.Add(thumb);
        }
        grid.MouseLeftButtonDown += Element_OnMouseLeftButtonDown;
        grid.MouseMove += Element_OnMouseMove;
        grid.MouseLeftButtonUp += Element_OnMouseLeftButtonUp;
        return grid;
    }

    private FrameworkElement CreateElementVisual(LabelElementModel model, double scale, IReadOnlyDictionary<string, string>? row, bool preview)
    {
        var value = ResolveValue(model, row);
        FrameworkElement visual = model.Kind switch
        {
            LabelElementKind.Text => new TextBlock
            {
                Text = value,
                FontSize = Math.Max(5, model.FontSize * (preview ? scale / EditPixelsPerMm : PrintPixelsPerMm / 3.2)),
                Foreground = ParseBrush(model.Foreground),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            },
            LabelElementKind.Code128 => CreateCode128Visual(value, model.ShowHumanReadable, model.Width * scale, model.Height * scale),
            LabelElementKind.QrCode => CreateBarcodeImage(BarcodePresentationService.CreateQrCodePng(value, 700), Stretch.Uniform),
            LabelElementKind.Image => CreateBase64Image(model.ImageBase64),
            LabelElementKind.Line => new System.Windows.Shapes.Line
            {
                X1 = 0,
                Y1 = Math.Max(1, model.Height * scale / 2),
                X2 = Math.Max(1, model.Width * scale),
                Y2 = Math.Max(1, model.Height * scale / 2),
                Stroke = ParseBrush(model.Stroke),
                StrokeThickness = Math.Max(1, model.StrokeThickness * scale)
            },
            LabelElementKind.Rectangle => new Border
            {
                BorderBrush = ParseBrush(model.Stroke),
                BorderThickness = new Thickness(Math.Max(1, model.StrokeThickness * scale))
            },
            _ => new TextBlock { Text = value }
        };
        visual.Width = Math.Max(1, model.Width * scale);
        visual.Height = Math.Max(1, model.Height * scale);
        visual.RenderTransformOrigin = new Point(0.5, 0.5);
        visual.RenderTransform = new RotateTransform(model.Rotation);
        return visual;
    }

    private static Image CreateBarcodeImage(byte[] bytes, Stretch stretch) => new()
    {
        Source = bytes.Length == 0 ? null : UiImage.FromBytes(bytes),
        Stretch = stretch
    };

    private static FrameworkElement CreateCode128Visual(string value, bool humanReadable, double width, double height)
    {
        if (!humanReadable)
        {
            return CreateBarcodeImage(BarcodePresentationService.CreateCode128Png(value, 900, 260), Stretch.Fill);
        }
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Math.Clamp(height * 0.22, 9, 24)) });
        var barcode = CreateBarcodeImage(BarcodePresentationService.CreateCode128Png(value, 900, 240), Stretch.Fill);
        grid.Children.Add(barcode);
        var text = new TextBlock
        {
            Text = value,
            FontSize = Math.Clamp(height * 0.16, 7, 14),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private static Image CreateBase64Image(string base64)
    {
        try
        {
            return new Image { Source = string.IsNullOrWhiteSpace(base64) ? null : UiImage.FromBytes(Convert.FromBase64String(base64)), Stretch = Stretch.Uniform };
        }
        catch
        {
            return new Image { Stretch = Stretch.Uniform };
        }
    }

    private static Brush ParseBrush(string value)
    {
        try { return (Brush)new BrushConverter().ConvertFromString(value)!; }
        catch { return Brushes.Black; }
    }

    private string ResolveValue(LabelElementModel model, IReadOnlyDictionary<string, string>? row)
    {
        if (!string.IsNullOrWhiteSpace(model.DataField) && row?.TryGetValue(model.DataField, out var fieldValue) == true)
        {
            return fieldValue;
        }
        return model.Content;
    }

    private IReadOnlyDictionary<string, string>? CurrentDataRow() => _dataRows.Count == 0 ? null : _dataRows[Math.Clamp(_dataRowIndex, 0, _dataRows.Count - 1)];

    private void AddElement(LabelElementKind kind, string content, double width, double height)
    {
        PushUndo();
        var model = new LabelElementModel { Kind = kind, Content = content, Width = width, Height = height, X = 4, Y = 4 };
        _document.Elements.Add(model);
        _selected = model;
        RenderCanvas();
        LoadSelectedProperties();
    }

    private void AddText_OnClick(object sender, RoutedEventArgs e) => AddElement(LabelElementKind.Text, "请输入文本", 28, 7);
    private void AddCode128_OnClick(object sender, RoutedEventArgs e) => AddElement(LabelElementKind.Code128, "690123456789", 38, 12);
    private void AddQr_OnClick(object sender, RoutedEventArgs e) => AddElement(LabelElementKind.QrCode, "https://example.com", 16, 16);
    private void AddIssueCommand_OnClick(object sender, RoutedEventArgs e)
    {
        if (IssueCommandSelector.SelectedItem is IssueCommandChoice choice)
        {
            AddElement(LabelElementKind.Code128, choice.Value, 40, 13);
        }
    }
    private void AddLine_OnClick(object sender, RoutedEventArgs e) => AddElement(LabelElementKind.Line, string.Empty, 35, 1);
    private void AddRectangle_OnClick(object sender, RoutedEventArgs e) => AddElement(LabelElementKind.Rectangle, string.Empty, 25, 12);

    private void AddImage_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp" };
        if (dialog.ShowDialog() != true) return;
        PushUndo();
        var model = new LabelElementModel
        {
            Kind = LabelElementKind.Image,
            Content = Path.GetFileName(dialog.FileName),
            ImageBase64 = Convert.ToBase64String(File.ReadAllBytes(dialog.FileName)),
            Width = 20,
            Height = 12
        };
        _document.Elements.Add(model);
        _selected = model;
        RenderCanvas();
        LoadSelectedProperties();
    }

    private void Element_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not LabelElementModel model) return;
        _selected = model;
        _dragStart = e.GetPosition(DesignCanvas);
        _dragX = model.X;
        _dragY = model.Y;
        PushUndo();
        element.CaptureMouse();
        RenderCanvas();
        LoadSelectedProperties();
        e.Handled = true;
    }

    private void Element_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element || !element.IsMouseCaptured || _selected is null || e.LeftButton != MouseButtonState.Pressed) return;
        var scale = EditPixelsPerMm * ZoomSlider.Value;
        var current = e.GetPosition(DesignCanvas);
        _selected.X = Math.Clamp(_dragX + (current.X - _dragStart.X) / scale, 0, Math.Max(0, _document.WidthMm - _selected.Width));
        _selected.Y = Math.Clamp(_dragY + (current.Y - _dragStart.Y) / scale, 0, Math.Max(0, _document.HeightMm - _selected.Height));
        Canvas.SetLeft(element, _selected.X * scale);
        Canvas.SetTop(element, _selected.Y * scale);
        LoadSelectedProperties();
    }

    private void Element_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element) element.ReleaseMouseCapture();
    }

    private void ResizeSelected(double dx, double dy)
    {
        if (_selected is null) return;
        _selected.Width = Math.Clamp(_selected.Width + dx, 2, _document.WidthMm - _selected.X);
        _selected.Height = Math.Clamp(_selected.Height + dy, 1, _document.HeightMm - _selected.Y);
        RenderCanvas();
        LoadSelectedProperties();
    }

    private void Canvas_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource != DesignCanvas) return;
        _selected = null;
        RenderCanvas();
        LoadSelectedProperties();
    }

    private void Delete_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        PushUndo();
        _document.Elements.Remove(_selected);
        _selected = null;
        RenderCanvas();
        LoadSelectedProperties();
    }

    private void Copy_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selected is not null) _clipboard = LabelTemplateStore.Clone(new LabelTemplateDocument { Elements = [_selected] }).Elements[0];
    }

    private void Paste_OnClick(object sender, RoutedEventArgs e)
    {
        if (_clipboard is null) return;
        PushUndo();
        var copy = LabelTemplateStore.Clone(new LabelTemplateDocument { Elements = [_clipboard] }).Elements[0];
        copy.Id = Guid.NewGuid();
        copy.X = Math.Min(copy.X + 2, Math.Max(0, _document.WidthMm - copy.Width));
        copy.Y = Math.Min(copy.Y + 2, Math.Max(0, _document.HeightMm - copy.Height));
        _document.Elements.Add(copy);
        _selected = copy;
        RenderCanvas();
        LoadSelectedProperties();
    }

    private void LayerUp_OnClick(object sender, RoutedEventArgs e) => MoveLayer(1);
    private void LayerDown_OnClick(object sender, RoutedEventArgs e) => MoveLayer(-1);
    private void MoveLayer(int direction)
    {
        if (_selected is null) return;
        var index = _document.Elements.IndexOf(_selected);
        var target = Math.Clamp(index + direction, 0, _document.Elements.Count - 1);
        if (target == index) return;
        PushUndo();
        _document.Elements.RemoveAt(index);
        _document.Elements.Insert(target, _selected);
        RenderCanvas();
    }

    private void AlignCenter_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        PushUndo();
        _selected.X = Math.Max(0, (_document.WidthMm - _selected.Width) / 2);
        RenderCanvas();
        LoadSelectedProperties();
    }

    private void AlignMiddle_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        PushUndo();
        _selected.Y = Math.Max(0, (_document.HeightMm - _selected.Height) / 2);
        RenderCanvas();
        LoadSelectedProperties();
    }

    private void PushUndo()
    {
        if (_loading) return;
        var state = LabelTemplateStore.Serialize(_document);
        if (_undo.Count == 0 || _undo.Peek() != state) _undo.Push(state);
        _redo.Clear();
    }

    private void Undo_OnClick(object sender, RoutedEventArgs e)
    {
        if (_undo.Count == 0) return;
        _redo.Push(LabelTemplateStore.Serialize(_document));
        LoadHistoryState(_undo.Pop());
    }

    private void Redo_OnClick(object sender, RoutedEventArgs e)
    {
        if (_redo.Count == 0) return;
        _undo.Push(LabelTemplateStore.Serialize(_document));
        LoadHistoryState(_redo.Pop());
    }

    private void LoadHistoryState(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"uv-label-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, json);
            var document = LabelTemplateStore.ReadAsync(path).GetAwaiter().GetResult();
            var undo = _undo.ToArray();
            var redo = _redo.ToArray();
            LoadDocument(document);
            foreach (var entry in undo.Reverse()) _undo.Push(entry);
            foreach (var entry in redo.Reverse()) _redo.Push(entry);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private void LoadSelectedProperties()
    {
        _loading = true;
        ElementContentInput.IsEnabled = _selected is not null;
        DataFieldSelector.IsEnabled = _selected is not null;
        if (_selected is null)
        {
            ElementContentInput.Text = ElementXInput.Text = ElementYInput.Text = ElementWidthInput.Text = ElementHeightInput.Text = RotationInput.Text = FontSizeInput.Text = string.Empty;
            HumanReadableCheck.IsChecked = false;
        }
        else
        {
            ElementContentInput.Text = _selected.Content;
            ElementXInput.Text = FormatNumber(_selected.X);
            ElementYInput.Text = FormatNumber(_selected.Y);
            ElementWidthInput.Text = FormatNumber(_selected.Width);
            ElementHeightInput.Text = FormatNumber(_selected.Height);
            RotationInput.Text = FormatNumber(_selected.Rotation);
            FontSizeInput.Text = FormatNumber(_selected.FontSize);
            HumanReadableCheck.IsChecked = _selected.ShowHumanReadable;
            DataFieldSelector.SelectedItem = _selected.DataField;
        }
        _loading = false;
    }

    private void ElementProperty_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _selected is null) return;
        PushUndo();
        _selected.Content = ElementContentInput.Text;
        _selected.DataField = DataFieldSelector.SelectedItem as string ?? string.Empty;
        if (TryNumber(ElementXInput.Text, out var x)) _selected.X = Math.Clamp(x, 0, _document.WidthMm);
        if (TryNumber(ElementYInput.Text, out var y)) _selected.Y = Math.Clamp(y, 0, _document.HeightMm);
        if (TryNumber(ElementWidthInput.Text, out var width)) _selected.Width = Math.Clamp(width, 1, _document.WidthMm);
        if (TryNumber(ElementHeightInput.Text, out var height)) _selected.Height = Math.Clamp(height, 1, _document.HeightMm);
        if (TryNumber(RotationInput.Text, out var rotation)) _selected.Rotation = rotation % 360;
        if (TryNumber(FontSizeInput.Text, out var fontSize)) _selected.FontSize = Math.Clamp(fontSize, 5, 96);
        _selected.ShowHumanReadable = HumanReadableCheck.IsChecked == true;
        RenderCanvas();
    }

    private void TemplateProperty_OnChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        PushUndo();
        _document.Name = TemplateNameInput.Text.Trim();
        if (TryNumber(WidthInput.Text, out var width)) _document.WidthMm = Math.Clamp(width, 10, 300);
        if (TryNumber(HeightInput.Text, out var height)) _document.HeightMm = Math.Clamp(height, 10, 300);
        if (TryNumber(OffsetXInput.Text, out var offsetX)) _document.OffsetXmm = Math.Clamp(offsetX, -30, 30);
        if (TryNumber(OffsetYInput.Text, out var offsetY)) _document.OffsetYmm = Math.Clamp(offsetY, -30, 30);
        RenderCanvas();
    }

    private void SizePresetSelector_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SizePresetSelector.SelectedItem is not ComboBoxItem item || item.Tag is not string tag || tag == "custom") return;
        var parts = tag.Split(',');
        WidthInput.Text = parts[0];
        HeightInput.Text = parts[1];
    }

    private void SelectPreset(double width, double height)
    {
        var tag = Math.Abs(width - 50) < 0.01 && Math.Abs(height - 30) < 0.01 ? "50,30" : Math.Abs(width - 60) < 0.01 && Math.Abs(height - 40) < 0.01 ? "60,40" : "custom";
        SizePresetSelector.SelectedItem = SizePresetSelector.Items.Cast<ComboBoxItem>().First(item => Equals(item.Tag, tag));
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_document.Name)) throw new InvalidOperationException("请先填写模板名称。");
            await _store.SaveAsync(_document);
            RefreshTemplateList(_document.Name);
            MessageBox.Show("模板已保存到本机模板库。", "电商拆包智能录像", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "保存模板失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void New_OnClick(object sender, RoutedEventArgs e) => LoadDocument(new LabelTemplateDocument());

    private async void TemplateSelector_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TemplateSelector.SelectedItem is not string name) return;
        try { LoadDocument(await _store.LoadAsync(name)); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "打开模板失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void RefreshTemplateList(string? selected = null)
    {
        _loading = true;
        TemplateSelector.ItemsSource = _store.ListNames();
        if (selected is not null) TemplateSelector.SelectedItem = selected;
        _loading = false;
    }

    private async void ImportTemplate_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "电商拆包智能录像标签模板|*.json" };
        if (dialog.ShowDialog() != true) return;
        try { LoadDocument(await LabelTemplateStore.ReadAsync(dialog.FileName)); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "导入模板失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ExportTemplate_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "电商拆包智能录像标签模板|*.json", FileName = _document.Name + ".json" };
        if (dialog.ShowDialog() != true) return;
        File.WriteAllText(dialog.FileName, LabelTemplateStore.Serialize(_document));
    }

    private void ZoomSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DesignCanvas is null) return;
        RenderCanvas();
    }

    private void ImportData_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "表格数据|*.xlsx;*.csv|Excel 工作簿|*.xlsx|CSV 文件|*.csv" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            _dataRows = Path.GetExtension(dialog.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase)
                ? ReadCsv(dialog.FileName)
                : ReadExcel(dialog.FileName);
            _dataRowIndex = 0;
            RefreshDataFields();
            RenderCanvas();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "导入批量数据失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private static List<Dictionary<string, string>> ReadCsv(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0) return [];
        var delimiter = lines[0].Count(character => character == '\t') > lines[0].Count(character => character == ',') ? '\t' : ',';
        var headers = ParseDelimitedLine(lines[0], delimiter);
        return lines.Skip(1).Where(line => !string.IsNullOrWhiteSpace(line)).Select(line =>
        {
            var values = ParseDelimitedLine(line, delimiter);
            return headers.Select((header, index) => (header, value: index < values.Count ? values[index] : string.Empty))
                .ToDictionary(pair => pair.header, pair => pair.value, StringComparer.CurrentCultureIgnoreCase);
        }).ToList();
    }

    private static List<string> ParseDelimitedLine(string line, char delimiter)
    {
        var values = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"' && quoted && index + 1 < line.Length && line[index + 1] == '"') { current.Append('"'); index++; }
            else if (character == '"') quoted = !quoted;
            else if (character == delimiter && !quoted) { values.Add(current.ToString()); current.Clear(); }
            else current.Append(character);
        }
        values.Add(current.ToString());
        return values;
    }

    private static List<Dictionary<string, string>> ReadExcel(string path)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        var workbook = document.WorkbookPart ?? throw new InvalidDataException("Excel 工作簿无效。");
        var sheet = workbook.Workbook?.Sheets?.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>().FirstOrDefault() ?? throw new InvalidDataException("Excel 中没有工作表。");
        if (sheet.Id?.Value is not string relationshipId) throw new InvalidDataException("Excel 工作表关系无效。");
        var worksheet = (WorksheetPart)workbook.GetPartById(relationshipId);
        var rows = worksheet.Worksheet?.GetFirstChild<DocumentFormat.OpenXml.Spreadsheet.SheetData>()?.Elements<DocumentFormat.OpenXml.Spreadsheet.Row>().ToList() ?? [];
        if (rows.Count == 0) return [];
        string Value(DocumentFormat.OpenXml.Spreadsheet.Cell cell)
        {
            var value = cell.CellValue?.InnerText ?? cell.InnerText;
            if (cell.DataType?.Value == DocumentFormat.OpenXml.Spreadsheet.CellValues.SharedString && int.TryParse(value, out var index))
                return workbook.SharedStringTablePart?.SharedStringTable?.Elements<DocumentFormat.OpenXml.Spreadsheet.SharedStringItem>().ElementAtOrDefault(index)?.InnerText ?? value;
            return value;
        }
        var headers = rows[0].Elements<DocumentFormat.OpenXml.Spreadsheet.Cell>().Select(Value).ToList();
        return rows.Skip(1).Select(row => row.Elements<DocumentFormat.OpenXml.Spreadsheet.Cell>().Select(Value).ToList())
            .Where(values => values.Any(value => !string.IsNullOrWhiteSpace(value)))
            .Select(values => headers.Select((header, index) => (header, value: index < values.Count ? values[index] : string.Empty))
                .Where(pair => !string.IsNullOrWhiteSpace(pair.header))
                .ToDictionary(pair => pair.header, pair => pair.value, StringComparer.CurrentCultureIgnoreCase)).ToList();
    }

    private void RefreshDataFields()
    {
        var fields = _dataRows.FirstOrDefault()?.Keys.OrderBy(value => value).ToList() ?? [];
        fields.Insert(0, string.Empty);
        DataFieldSelector.ItemsSource = fields;
        DataSummaryText.Text = _dataRows.Count == 0 ? "未导入数据" : $"已导入 {_dataRows.Count} 行 · 预览第 {_dataRowIndex + 1} 行";
        LoadSelectedProperties();
    }

    private void PreviousRow_OnClick(object sender, RoutedEventArgs e) { if (_dataRows.Count == 0) return; _dataRowIndex = Math.Max(0, _dataRowIndex - 1); RefreshDataFields(); RenderCanvas(); }
    private void NextRow_OnClick(object sender, RoutedEventArgs e) { if (_dataRows.Count == 0) return; _dataRowIndex = Math.Min(_dataRows.Count - 1, _dataRowIndex + 1); RefreshDataFields(); RenderCanvas(); }

    private void Print_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return;
        try
        {
            var rows = GetRowsForPrint();
            var copies = int.TryParse(CopiesInput.Text, out var copyCount) ? Math.Clamp(copyCount, 1, 999) : 1;
            var document = BuildPrintDocument(rows, copies);
            dialog.PrintTicket.PageMediaSize = new PageMediaSize(_document.WidthMm * PrintPixelsPerMm, _document.HeightMm * PrintPixelsPerMm);
            dialog.PrintDocument(document.DocumentPaginator, $"{_document.Name} - 电商拆包智能录像");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "打印失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private IReadOnlyList<IReadOnlyDictionary<string, string>?> GetRowsForPrint()
    {
        if (_dataRows.Count == 0) return new IReadOnlyDictionary<string, string>?[] { null };
        var start = int.TryParse(StartRowInput.Text, out var startValue) ? Math.Clamp(startValue, 1, _dataRows.Count) : 1;
        var end = int.TryParse(EndRowInput.Text, out var endValue) ? Math.Clamp(endValue, start, _dataRows.Count) : _dataRows.Count;
        return _dataRows.Skip(start - 1).Take(end - start + 1).Cast<IReadOnlyDictionary<string, string>?>().ToArray();
    }

    private FixedDocument BuildPrintDocument(IReadOnlyList<IReadOnlyDictionary<string, string>?> rows, int copies)
    {
        var fixedDocument = new FixedDocument();
        var pageWidth = _document.WidthMm * PrintPixelsPerMm;
        var pageHeight = _document.HeightMm * PrintPixelsPerMm;
        fixedDocument.DocumentPaginator.PageSize = new Size(pageWidth, pageHeight);
        foreach (var row in rows)
        foreach (var _ in Enumerable.Range(0, copies))
        {
            var page = new FixedPage { Width = pageWidth, Height = pageHeight, Background = Brushes.White };
            foreach (var model in _document.Elements)
            {
                var visual = CreateElementVisual(model, PrintPixelsPerMm, row, false);
                FixedPage.SetLeft(visual, (model.X + _document.OffsetXmm) * PrintPixelsPerMm);
                FixedPage.SetTop(visual, (model.Y + _document.OffsetYmm) * PrintPixelsPerMm);
                page.Children.Add(visual);
            }
            var content = new PageContent();
            ((System.Windows.Markup.IAddChild)content).AddChild(page);
            fixedDocument.Pages.Add(content);
        }
        return fixedDocument;
    }

    private static string FormatNumber(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);
    private static bool TryNumber(string text, out double value) => double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
