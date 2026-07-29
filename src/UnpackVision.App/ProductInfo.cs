using System.Reflection;

namespace UnpackVision.App;

internal static class ProductInfo
{
    internal const string Name = "电商拆包智能录像";
    internal const string Developer = "五成";
    internal const string RepositoryUrl = "https://github.com/907609732/unpack-vision";
    internal const string LatestReleaseUrl = RepositoryUrl + "/releases/latest";
    internal const string DesktopManifestUrl =
        LatestReleaseUrl + "/download/desktop-update.json";
    internal const string WindowsDownloadUrl =
        RepositoryUrl + "/releases/latest/download/EcommerceUnpackRecorder-win-Setup.exe";
    internal const string AndroidDownloadUrl =
        RepositoryUrl + "/releases/latest/download/EcommerceUnpackRecorder-Android.apk";

    internal static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "2.2.0";
}
