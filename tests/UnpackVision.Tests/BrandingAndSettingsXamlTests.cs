using System.Resources;
using System.Xml.Linq;
namespace UnpackVision.Tests;

public sealed class BrandingAndSettingsXamlTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void BrandLogo_IsEmbeddedInTheWpfAssembly()
    {
        var assembly = typeof(global::UnpackVision.App.App).Assembly;
        var resourceName = Assert.Single(
            assembly.GetManifestResourceNames(),
            name => name.EndsWith(".g.resources", StringComparison.OrdinalIgnoreCase));

        using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new ResourceReader(stream);
        var keys = reader.Cast<System.Collections.DictionaryEntry>()
            .Select(entry => entry.Key?.ToString())
            .Where(key => key is not null)
            .ToArray();

        Assert.Contains(
            keys,
            key => string.Equals(
                key,
                "assets/ecommerceunpackrecorder-logo.png",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BrandLogo_UsesOneSharedPackResourceAcrossWindows()
    {
        var appDocument = Load("App.xaml");
        var logo = appDocument.Descendants(Presentation + "BitmapImage")
            .Single(element => (string?)element.Attribute(Xaml + "Key") == "AppBrandLogo");

        Assert.Equal(
            "pack://application:,,,/Assets/EcommerceUnpackRecorder-Logo.png",
            (string?)logo.Attribute("UriSource"));

        foreach (var fileName in new[]
                 {
                     "MainWindow.xaml",
                     "FirstRunConsentWindow.xaml",
                     "SetupWizardWindow.xaml"
                 })
        {
            var sources = Load(fileName).Descendants(Presentation + "Image")
                .Select(element => (string?)element.Attribute("Source"));
            Assert.Contains("{StaticResource AppBrandLogo}", sources);
        }
    }

    [Fact]
    public void ProjectDeclaresTheLogoAsAWpfResource()
    {
        var project = Load("UnpackVision.App.csproj");
        var resource = project.Descendants("Resource")
            .Single(element => ((string?)element.Attribute("Include"))?.EndsWith(
                "EcommerceUnpackRecorder-Logo.png",
                StringComparison.OrdinalIgnoreCase) == true);

        Assert.NotNull(resource);
    }

    [Fact]
    public void SettingsWindow_PreservesNavigationAndOperationalControlContracts()
    {
        var document = Load("SettingsWindow.xaml");
        var tabControl = document.Descendants(Presentation + "TabControl")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "SettingsTabControl");
        var headers = tabControl.Elements(Presentation + "TabItem")
            .Select(element => (string?)element.Attribute("Header"))
            .ToArray();

        Assert.Equal(
            new[] { "存储与数据", "相机信息", "单号配置", "异常标签", "手机协同", "关于", "使用帮助" },
            headers);

        var names = document.Descendants()
            .Select(element => (string?)element.Attribute(Xaml + "Name"))
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);
        var requiredNames = new[]
        {
            "RecordingRootInput", "ExcelPathInput", "WorkspaceStatusText", "MaximumMinutesInput",
            "LivePreviewCheck", "VoiceCheck", "VoiceVolumeSlider", "CameraSourceKindInput",
            "CameraIndexInput", "AutoBestCameraCheck", "NetworkStreamUrlInput", "HikvisionHostInput",
            "HikvisionPortInput", "HikvisionChannelInput", "HikvisionStreamInput", "NetworkUsernameInput",
            "NetworkPasswordInput", "WidthInput", "HeightInput", "FpsInput", "AutoFocusCheck",
            "BrightnessSlider", "ContrastSlider", "SharpnessSlider", "SaturationSlider",
            "MinimumLengthInput", "MaximumLengthInput", "FilterPrefixCheck", "PrefixInput",
            "FilterSuffixCheck", "SuffixInput", "DebounceInput", "CaptureIssueSnapshotCheck",
            "IssueTagsGrid", "AboutVersionText", "AutoUpdateCheck", "UpdateStatusText",
            "UpdateProgress", "InstallUpdateButton", "RepositoryUrlText", "AndroidDownloadQr",
            "AndroidDownloadUrlText", "TelemetryCheck"
        };

        Assert.All(requiredNames, name => Assert.Contains(name, names));
        Assert.Equal("1120", (string?)document.Root?.Attribute("Width"));
        Assert.Equal("960", (string?)document.Root?.Attribute("MinWidth"));
    }

    [Fact]
    public void SettingsWindow_UsesScopedNavigationCardsAndFixedActionBar()
    {
        var window = Load("SettingsWindow.xaml");
        var styles = Load("SettingsWindowStyles.xaml");
        var styleKeys = styles.Descendants(Presentation + "Style")
            .Select(element => (string?)element.Attribute(Xaml + "Key"))
            .Where(key => key is not null)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("SettingsTabControlStyle", styleKeys);
        Assert.Contains("SettingsNavTabStyle", styleKeys);
        Assert.Contains("SettingsCard", styleKeys);
        Assert.Contains("SettingsActionBar", styleKeys);

        var actionBar = window.Descendants(Presentation + "Border")
            .Single(element => (string?)element.Attribute("Style") == "{StaticResource SettingsActionBar}");
        Assert.Equal("1", (string?)actionBar.Attribute("Grid.Row"));

        var sourceSelector = window.Descendants(Presentation + "ComboBox")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "CameraSourceKindInput");
        Assert.Equal(
            "CameraSourceKindInput_OnSelectionChanged",
            (string?)sourceSelector.Attribute("SelectionChanged"));
    }

    private static XDocument Load(string fileName) =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestData", fileName));
}
