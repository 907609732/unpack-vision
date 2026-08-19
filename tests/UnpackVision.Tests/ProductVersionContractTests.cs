using System.Xml.Linq;

namespace UnpackVision.Tests;

public sealed class ProductVersionContractTests
{
    private const string Version = "2.5.3";

    [Fact]
    public void WindowsExecutableProjectsAndProductFallbackUseOneVersion()
    {
        foreach (var fileName in new[]
                 {
                     "UnpackVision.App.csproj",
                     "UnpackVision.Service.csproj",
                     "UnpackVision.StationHost.csproj"
                 })
        {
            var project = XDocument.Load(TestData(fileName));
            Assert.Equal(Version, project.Descendants("Version").Single().Value);
        }

        var productInfo = File.ReadAllText(TestData("ProductInfo.cs"));
        Assert.Contains($": \"{Version}\";", productInfo, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidKeepsCompatibleApplicationIdAndUsesReleaseVersionCode()
    {
        var gradle = File.ReadAllText(TestData("android-app-build.gradle.kts"));
        Assert.Contains("applicationId = \"com.unpackvision.mobile\"", gradle, StringComparison.Ordinal);
        Assert.Contains("versionCode = 20503", gradle, StringComparison.Ordinal);
        Assert.Contains($"versionName = \"{Version}\"", gradle, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseNotesKeepUnsignedWindowsBuildInPrereleaseChannel()
    {
        var notes = File.ReadAllText(TestData("release-2.5.3.md"));
        Assert.Contains("2.5.3 预发布说明", notes, StringComparison.Ordinal);
        Assert.Contains("未取得可信 Authenticode 代码签名", notes, StringComparison.Ordinal);
        Assert.Contains("不能标记为正式稳定版", notes, StringComparison.Ordinal);
    }

    private static string TestData(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "TestData", fileName);
}
