using System.Xml.Linq;

namespace UnpackVision.Tests;

public sealed class MainWindowXamlBindingTests
{
    [Theory]
    [InlineData("TrackingNo")]
    [InlineData("TimeText")]
    [InlineData("FileSizeText")]
    public void RecentRecordingReadOnlyFields_UseOneWayBindings(string propertyName)
    {
        var xamlPath = Path.Combine(AppContext.BaseDirectory, "TestData", "MainWindow.xaml");
        var document = XDocument.Load(xamlPath);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var textBinding = document
            .Descendants(presentation + "TextBox")
            .Select(element => (string?)element.Attribute("Text"))
            .Single(value => value?.Contains($"Binding {propertyName}", StringComparison.Ordinal) == true);

        Assert.Contains("Mode=OneWay", textBinding, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("HistoryWindow.xaml")]
    [InlineData("PairedDevicesWindow.xaml")]
    public void SelectableReadOnlyColumns_UseOneWayBindings(string fileName)
    {
        var xamlPath = Path.Combine(AppContext.BaseDirectory, "TestData", fileName);
        var document = XDocument.Load(xamlPath);
        var bindings = document
            .Descendants()
            .Where(element => element.Name.LocalName == "SelectableTextColumn")
            .Select(element => (string?)element.Attribute("Binding"))
            .ToArray();

        Assert.NotEmpty(bindings);
        Assert.All(
            bindings,
            binding => Assert.Contains("Mode=OneWay", binding, StringComparison.Ordinal));
    }

    [Fact]
    public void HistorySelectionCheckboxes_UseTheDataGridSelectionState()
    {
        var xamlPath = Path.Combine(AppContext.BaseDirectory, "TestData", "HistoryWindow.xaml");
        var document = XDocument.Load(xamlPath);

        var header = document
            .Descendants()
            .Single(element => (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) ==
                               "SelectAllRowsCheckBox");
        var rowCheckbox = document
            .Descendants()
            .Where(element => element.Name.LocalName == "CheckBox")
            .Single(element => ((string?)element.Attribute("IsChecked"))?.Contains(
                "AncestorType={x:Type DataGridRow}",
                StringComparison.Ordinal) == true);

        Assert.Equal("True", (string?)header.Attribute("IsThreeState"));
        Assert.Equal("SelectAllRowsCheckBox_OnClick", (string?)header.Attribute("Click"));
        var binding = (string?)rowCheckbox.Attribute("IsChecked");
        Assert.Contains("IsSelected", binding, StringComparison.Ordinal);
        Assert.Contains("Mode=TwoWay", binding, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoryToolbar_ExposesExcelFolderAction()
    {
        var xamlPath = Path.Combine(AppContext.BaseDirectory, "TestData", "HistoryWindow.xaml");
        var document = XDocument.Load(xamlPath);

        var button = document
            .Descendants()
            .Single(element => (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) ==
                               "OpenExcelFolderButton");

        Assert.Equal("Excel 文件夹", (string?)button.Attribute("Content"));
        Assert.Equal("OpenExcelFolder_OnClick", (string?)button.Attribute("Click"));
    }
}
