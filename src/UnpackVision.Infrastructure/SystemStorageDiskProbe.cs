using System.Runtime.InteropServices;
using System.Text;
using UnpackVision.Core.Recording;

namespace UnpackVision.Infrastructure;

/// <summary>
/// Checks a configured root without creating directories or retaining probe data. A unique
/// one-byte file is created and removed to prove write access; failures never modify media.
/// </summary>
public sealed class SystemStorageDiskProbe : IStorageDiskProbe
{
    public async ValueTask<StorageDiskProbeResult> ProbeAsync(
        StorageTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(target.RootPath))
        {
            return Offline("The recording root path is empty.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(target.RootPath.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Offline("The recording root path is invalid.");
        }

        if (!Directory.Exists(fullPath))
        {
            return Offline("The recording root does not exist.");
        }

        if (!TryGetCapacity(fullPath, out var totalBytes, out var availableBytes, out var capacityMessage))
        {
            return Offline(capacityMessage ?? "The recording volume is not ready.");
        }

        var volumeId = ResolveVolumeId(fullPath);
        var (isWritable, writeMessage) = await TestWriteAccessAsync(fullPath, cancellationToken);
        return new StorageDiskProbeResult(
            true,
            isWritable,
            volumeId,
            totalBytes,
            availableBytes,
            writeMessage);
    }

    private static bool TryGetCapacity(
        string fullPath,
        out long totalBytes,
        out long availableBytes,
        out string? message)
    {
        totalBytes = 0;
        availableBytes = 0;
        message = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (!GetDiskFreeSpaceExW(
                        fullPath,
                        out var availableToCaller,
                        out var total,
                        out _))
                {
                    message = "Windows could not read the recording volume capacity.";
                    return false;
                }

                totalBytes = ClampToInt64(total);
                availableBytes = ClampToInt64(availableToCaller);
                return true;
            }

            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                message = "The recording volume root could not be resolved.";
                return false;
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                message = "The recording volume is not ready.";
                return false;
            }

            totalBytes = drive.TotalSize;
            availableBytes = drive.AvailableFreeSpace;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            message = "The recording volume capacity could not be read.";
            return false;
        }
    }

    private static async Task<(bool IsWritable, string? Message)> TestWriteAccessAsync(
        string fullPath,
        CancellationToken cancellationToken)
    {
        var probePath = Path.Combine(fullPath, $".unpackvision-write-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             probePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(new byte[] { 0x55 }, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            return (true, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return (false, "The recording root is online but is not writable.");
        }
        finally
        {
            try
            {
                if (File.Exists(probePath))
                {
                    File.Delete(probePath);
                }
            }
            catch
            {
                // A failed cleanup must not turn a proven writable target into a false offline result.
            }
        }
    }

    private static string ResolveVolumeId(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? fullPath;
        if (!OperatingSystem.IsWindows() || root.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return Path.TrimEndingDirectorySeparator(root);
        }

        var mountPoint = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        var buffer = new StringBuilder(128);
        return GetVolumeNameForVolumeMountPointW(mountPoint, buffer, (uint)buffer.Capacity)
            ? Path.TrimEndingDirectorySeparator(buffer.ToString())
            : Path.TrimEndingDirectorySeparator(root);
    }

    private static StorageDiskProbeResult Offline(string message) =>
        new(false, false, string.Empty, 0, 0, message);

    private static long ClampToInt64(ulong value) =>
        value > long.MaxValue ? long.MaxValue : (long)value;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(
        string directoryName,
        out ulong freeBytesAvailableToCaller,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(
        string volumeMountPoint,
        StringBuilder volumeName,
        uint bufferLength);
}
