using System.Reflection;
using System.IO;

namespace UnpackVision.App;

internal static class ProductInfo
{
    internal const string Name = "拆包智录";
    internal const string Developer = "五成";
    internal const string RepositoryUrl = "https://github.com/907609732/unpack-vision";
    internal const string LatestReleaseUrl = RepositoryUrl + "/releases/latest";
    internal const string DesktopManifestUrl =
        LatestReleaseUrl + "/download/desktop-update.json";
    internal const string WindowsDownloadUrl =
        RepositoryUrl + "/releases/latest/download/EcommerceUnpackRecorder-win-Setup.exe";
    internal const string AndroidDownloadUrl =
        RepositoryUrl + "/releases/latest/download/EcommerceUnpackRecorder-Android.apk";
    internal static string TelemetryEndpoint =>
        Environment.GetEnvironmentVariable("UNPACKVISION_TELEMETRY_ENDPOINT") ??
        ReadOptionalAsset("Assets/telemetry-endpoint.txt");

    internal static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "2.3.2";

    private static string ReadOptionalAsset(string relativePath)
    {
        var path = Path.Combine(AppContext.BaseDirectory, relativePath);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
    }
}
