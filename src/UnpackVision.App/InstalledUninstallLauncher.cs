using System.Diagnostics;
using System.IO;

namespace UnpackVision.App;

internal static class InstalledUninstallLauncher
{
    internal static string? FindUpdater(string? applicationDirectory = null)
    {
        var currentDirectory = Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Directory.GetParent(currentDirectory)?.FullName;
        if (parent is null) return null;
        var updater = Path.Combine(parent, "Update.exe");
        return File.Exists(updater) ? updater : null;
    }

    internal static void Start(string updaterPath)
    {
        var info = new ProcessStartInfo
        {
            FileName = updaterPath,
            WorkingDirectory = Path.GetDirectoryName(updaterPath)!,
            UseShellExecute = true
        };
        info.ArgumentList.Add("uninstall");
        _ = Process.Start(info) ?? throw new InvalidOperationException("无法启动 Velopack 卸载程序");
    }
}
