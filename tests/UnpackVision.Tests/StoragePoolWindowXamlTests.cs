using System.Xml.Linq;

namespace UnpackVision.Tests;

public sealed class StoragePoolWindowXamlTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void SettingsStoragePage_ExposesPoolSummaryAndManager()
    {
        var settings = Load("SettingsWindow.xaml");
        var names = settings.Descendants()
            .Select(element => (string?)element.Attribute(Xaml + "Name"))
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("StoragePoolSummaryText", names);
        Assert.Contains("ManageStoragePoolButton", names);
        var button = settings.Descendants(Presentation + "Button")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "ManageStoragePoolButton");
        Assert.Equal("ManageStoragePool_OnClick", (string?)button.Attribute("Click"));
    }

    [Fact]
    public void PoolManager_ProvidesUnlimitedListOrderingHealthAndSafeRemovalControls()
    {
        var window = Load("StoragePoolWindow.xaml");
        var names = window.Descendants()
            .Select(element => (string?)element.Attribute(Xaml + "Name"))
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(
            new[]
            {
                "StorageTargetsList", "AddStorageTargetButton", "RemoveStorageTargetButton",
                "MoveStorageTargetUpButton", "MoveStorageTargetDownButton", "RefreshStorageTargetsButton"
            },
            name => Assert.Contains(name, names));

        var sourceBinding = window.Descendants(Presentation + "ListBox")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "StorageTargetsList")
            .Attribute("ItemsSource")?.Value;
        Assert.Equal("{Binding Targets}", sourceBinding);
        var displayedText = string.Join(
            " ",
            window.Descendants()
                .SelectMany(element => element.Attributes())
                .Where(attribute => attribute.Name.LocalName is "Text" or "Content" or "ToolTip")
                .Select(attribute => attribute.Value));
        Assert.Contains("不会删除录像", displayedText);
    }

    private static XDocument Load(string fileName) =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestData", fileName));
}
