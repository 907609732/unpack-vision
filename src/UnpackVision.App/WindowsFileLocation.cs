using System.Diagnostics;
using System.IO;

namespace UnpackVision.App;

internal enum WindowsFileLocationState
{
    NotConfigured,
    InvalidPath,
    FileAvailable,
    DirectoryAvailable,
    MissingDirectory
}

internal readonly record struct WindowsFileLocationResult(
    WindowsFileLocationState State,
    string? FullPath,
    string? DirectoryPath);

/// <summary>
/// Resolves and opens Windows file locations without leaking private paths into UI logs.
/// </summary>
internal static class WindowsFileLocation
{
    internal static WindowsFileLocationResult Resolve(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return new WindowsFileLocationResult(WindowsFileLocationState.NotConfigured, null, null);
        }

        try
        {
            var fullPath = Path.GetFullPath(configuredPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return new WindowsFileLocationResult(WindowsFileLocationState.InvalidPath, fullPath, null);
            }
            if (File.Exists(fullPath))
            {
                return new WindowsFileLocationResult(
                    WindowsFileLocationState.FileAvailable,
                    fullPath,
                    directory);
            }
            return new WindowsFileLocationResult(
                Directory.Exists(directory)
                    ? WindowsFileLocationState.DirectoryAvailable
                    : WindowsFileLocationState.MissingDirectory,
                fullPath,
                directory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new WindowsFileLocationResult(WindowsFileLocationState.InvalidPath, null, null);
        }
    }

    internal static void SelectFile(string fullPath)
    {
        var startInfo = new ProcessStartInfo("explorer.exe")
        {
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add("/select,");
        startInfo.ArgumentList.Add(Path.GetFullPath(fullPath));
        Process.Start(startInfo);
    }

    internal static void OpenDirectory(string directoryPath)
    {
        var startInfo = new ProcessStartInfo("explorer.exe")
        {
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add(Path.GetFullPath(directoryPath));
        Process.Start(startInfo);
    }
}
