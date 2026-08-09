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
    public void SafeUninstallUiHasPreserveDefaultAndTwoDeletionWarnings()
    {
        var settings = Load("SettingsWindow.xaml");
        Assert.Contains(settings.Descendants(Presentation + "Button"), element =>
            (string?)element.Attribute("Content") == "安全卸载拆包智录" &&
            (string?)element.Attribute("Click") == "SafeUninstall_OnClick");

        var dialog = Load("SafeUninstallWindow.xaml");
        var keepOption = dialog.Descendants(Presentation + "RadioButton")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "KeepDataOption");
        Assert.Equal("True", (string?)keepOption.Attribute("IsChecked"));
        var code = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "SafeUninstallWindow.xaml.cs"));
        Assert.Contains("第一次警告", code);
        Assert.Contains("最后警告", code);
    }

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
            "SingleCameraModeOption", "MultiCameraModeOption", "CameraModeDescriptionText",
            "MultiCameraManagementPanel", "CameraRigList", "AddCameraButton", "CameraMoreActionsButton",
            "LocalCameraLabel", "LocalCameraPanel", "CameraIndexInput", "AutoBestCameraCheck",
            "NetworkConnectionCard", "NetworkStreamLabel", "NetworkStreamUrlInput",
            "HikvisionRecorderLabel", "HikvisionRecorderPanel", "HikvisionHostInput",
            "HikvisionHttpPortInput", "HikvisionPortInput", "HikvisionChannelInput", "HikvisionStreamInput", "NetworkUsernameInput",
            "NetworkCredentialsLabel", "NetworkCredentialsPanel", "HikvisionDiscoveryLabel", "HikvisionDiscoveryPanel",
            "DiscoverHikvisionChannelsButton", "HikvisionPreferSubStreamCheck", "HikvisionDiscoveryProgress",
            "HikvisionDiscoveryStatusText",
            "NetworkPasswordInput", "AdvancedRecordingExpander", "AdvancedPictureExpander",
            "WidthInput", "HeightInput", "FpsInput", "AutoFocusCheck",
            "BrightnessSlider", "ContrastSlider", "SharpnessSlider", "SaturationSlider",
            "TestCurrentCameraButton", "TestAllCamerasButton",
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

    [Fact]
    public void SettingsWindow_CameraNameUsesTheDeclaredInputStyle()
    {
        var window = Load("SettingsWindow.xaml");
        var styles = Load("SettingsWindowStyles.xaml");
        var cameraNameInput = window.Descendants(Presentation + "TextBox")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "CameraNameInput");

        Assert.Equal("{StaticResource SettingsInput}", (string?)cameraNameInput.Attribute("Style"));
        Assert.Contains(
            styles.Descendants(Presentation + "Style"),
            element => (string?)element.Attribute(Xaml + "Key") == "SettingsInput");
        Assert.DoesNotContain("SettingsTextBox", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "SettingsWindow.xaml")));
    }

    [Fact]
    public void SettingsWindow_UsesModeSwitchToKeepSingleCameraConfigurationFocused()
    {
        var window = Load("SettingsWindow.xaml");
        var single = window.Descendants(Presentation + "RadioButton")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "SingleCameraModeOption");
        var multi = window.Descendants(Presentation + "RadioButton")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "MultiCameraModeOption");
        var management = window.Descendants(Presentation + "StackPanel")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "MultiCameraManagementPanel");

        Assert.Equal("CameraRigMode_OnChecked", (string?)single.Attribute("Checked"));
        Assert.Equal("CameraRigMode_OnChecked", (string?)multi.Attribute("Checked"));
        Assert.Equal("Collapsed", (string?)management.Attribute("Visibility"));

        var discovery = window.Descendants(Presentation + "Button")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "DiscoverHikvisionChannelsButton");
        Assert.Equal("DiscoverHikvisionChannels_OnClick", (string?)discovery.Attribute("Click"));
    }

    [Fact]
    public void SettingsWindow_HidesSourceSpecificNetworkConfigurationUntilSelected()
    {
        var window = Load("SettingsWindow.xaml");
        var networkCard = window.Descendants(Presentation + "Border")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "NetworkConnectionCard");
        var localPanel = window.Descendants(Presentation + "StackPanel")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "LocalCameraPanel");

        Assert.Equal("Collapsed", (string?)networkCard.Attribute("Visibility"));
        Assert.Null(localPanel.Attribute("Visibility"));

        var code = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "SettingsWindow.xaml.cs"));
        Assert.Contains("NetworkConnectionCard.Visibility = isNetwork", code);
        Assert.Contains("NetworkStreamUrlInput.Visibility = isCustomNetwork", code);
        Assert.Contains("HikvisionDiscoveryPanel.Visibility = isHikvision", code);
        Assert.Contains("AutoFocusCheck.Visibility = isLocal", code);
    }

    [Fact]
    public void SettingsWindow_UsesThreeStepCameraFlowAndProgressiveDisclosure()
    {
        var window = Load("SettingsWindow.xaml");
        var text = window.Descendants(Presentation + "TextBlock")
            .Select(element => (string?)element.Attribute("Text"))
            .Where(value => value is not null)
            .ToArray();

        Assert.Contains("1  选择录像模式", text);
        Assert.Contains("2  配置当前机位", text);
        Assert.Contains("3  测试并保存", text);

        foreach (var name in new[] { "AdvancedRecordingExpander", "AdvancedPictureExpander" })
        {
            var expander = window.Descendants(Presentation + "Expander")
                .Single(element => (string?)element.Attribute(Xaml + "Name") == name);
            Assert.Equal("False", (string?)expander.Attribute("IsExpanded"));
        }

        var visibleButtonLabels = window.Descendants(Presentation + "Button")
            .Select(element => (string?)element.Attribute("Content"))
            .Where(value => value is not null)
            .ToArray();
        Assert.DoesNotContain("删除", visibleButtonLabels);
        Assert.DoesNotContain("设为主机位", visibleButtonLabels);
        Assert.DoesNotContain("上移", visibleButtonLabels);
        Assert.DoesNotContain("下移", visibleButtonLabels);

        var more = window.Descendants(Presentation + "Button")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "CameraMoreActionsButton");
        Assert.Equal("CameraMoreActions_OnClick", (string?)more.Attribute("Click"));
        var testAll = window.Descendants(Presentation + "Button")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "TestAllCamerasButton");
        Assert.Equal("Collapsed", (string?)testAll.Attribute("Visibility"));
    }

    [Fact]
    public void CameraTest_UsesDedicatedProgressWindowWithStagePercentAndRemainingTime()
    {
        var settings = Load("SettingsWindow.xaml");
        Assert.DoesNotContain(settings.Descendants(), element =>
            (string?)element.Attribute(Xaml + "Name") == "CameraTestProgressPanel");

        var dialog = Load("CameraTestProgressWindow.xaml");
        var progress = dialog.Descendants(Presentation + "ProgressBar")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "TestProgressBar");
        Assert.Equal("100", (string?)progress.Attribute("Maximum"));
        Assert.Contains(dialog.Descendants(Presentation + "TextBlock"), element =>
            (string?)element.Attribute(Xaml + "Name") == "StageText");
        Assert.Contains(dialog.Descendants(Presentation + "TextBlock"), element =>
            (string?)element.Attribute(Xaml + "Name") == "PercentText");
        Assert.Contains(dialog.Descendants(Presentation + "TextBlock"), element =>
            (string?)element.Attribute(Xaml + "Name") == "RemainingText");

        var settingsCode = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "SettingsWindow.xaml.cs"));
        var dialogCode = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "CameraTestProgressWindow.xaml.cs"));
        Assert.Contains("new CameraTestProgressWindow", settingsCode);
        Assert.Contains("CreateCameraTestProgress", settingsCode);
        Assert.Contains("TestCandidateCameraRigAsync(rig, progress)", settingsCode);
        Assert.Contains("本阶段预计还需", dialogCode);
        Assert.Contains("if (!_canClose)", dialogCode);
    }

    [Fact]
    public void SettingsWindow_ProvidesVisibleMigrationProgressInsteadOfOnlyChangingText()
    {
        var window = Load("SettingsWindow.xaml");
        var names = window.Descendants()
            .Select(element => (string?)element.Attribute(Xaml + "Name"))
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("MigrationProgressOverlay", names);
        Assert.Contains("MigrationProgressBar", names);
        Assert.Contains("MigrationProgressPercentText", names);
        Assert.Contains("MigrationProgressCountText", names);
        Assert.Contains("MigrationProgressDetailText", names);
    }

    private static XDocument Load(string fileName) =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestData", fileName));
}
