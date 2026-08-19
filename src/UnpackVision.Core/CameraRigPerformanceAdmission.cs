using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UnpackVision.Core.Recording;

namespace UnpackVision.Core;

/// <summary>
/// Produces a local, non-reversible admission key for the exact camera, composite and
/// enabled-storage configuration exercised by a performance test. The digest prevents a
/// stale passing result from authorizing a materially different production rig.
/// </summary>
public static class CameraRigPerformanceAdmission
{
    public static string ComputeFingerprint(CameraRigOptions rig, StoragePoolOptions storagePool)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(storagePool);

        var value = new StringBuilder("camera-rig-performance:v1");
        Append(value, rig.Mode);
        Append(value, rig.CompositeWidth);
        Append(value, rig.CompositeHeight);
        Append(value, rig.CompositeFramesPerSecond);
        Append(value, rig.EffectiveCompositeRecordingEnabled);
        Append(value, rig.CapacityProfileId);
        foreach (var camera in rig.EnabledCameras.OrderBy(camera => camera.SortOrder))
        {
            Append(value, camera.Id);
            Append(value, camera.IsPrimary);
            Append(value, camera.SortOrder);
            Append(value, camera.SourceType);
            Append(value, camera.WindowsSymbolicLink);
            Append(value, camera.LegacyCameraIndex);
            Append(value, camera.AutoSelectBestCamera);
            Append(value, camera.Width);
            Append(value, camera.Height);
            Append(value, camera.FramesPerSecond);
            Append(value, camera.Codec);
            Append(value, camera.RotationQuarterTurns);
            Append(value, camera.Mirror);
            Append(value, camera.NetworkStreamUrl);
            Append(value, camera.NetworkUsername);
            Append(value, camera.NetworkPasswordProtected);
            Append(value, camera.HikvisionHost);
            Append(value, camera.HikvisionHttpPort);
            Append(value, camera.HikvisionRtspPort);
            Append(value, camera.HikvisionChannel);
            Append(value, camera.HikvisionSubStream);
        }

        foreach (var target in (storagePool.Targets ?? [])
                     .Where(target => target.Enabled)
                     .OrderBy(target => target.Priority)
                     .ThenBy(target => target.Id, StringComparer.OrdinalIgnoreCase))
        {
            Append(value, target.Id);
            Append(value, target.Priority);
            Append(value, NormalizePath(target.RootPath));
            Append(value, target.VolumeId);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString()))).ToLowerInvariant();
    }

    public static bool IsCurrent(CameraRigOptions rig, StoragePoolOptions storagePool)
    {
        if (!rig.LastPerformanceTestPassed || string.IsNullOrWhiteSpace(rig.LastPerformanceTestFingerprint))
        {
            return false;
        }
        var actual = Encoding.ASCII.GetBytes(rig.LastPerformanceTestFingerprint);
        var expected = Encoding.ASCII.GetBytes(ComputeFingerprint(rig, storagePool));
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static void EnsureHighChannelRigIsAdmitted(CameraRigOptions rig, StoragePoolOptions storagePool)
    {
        if (rig.EnabledCameras.Count <= CameraRigOptions.LegacyCompatibilityMaximumCameraCount)
        {
            return;
        }

        if (!IsCurrent(rig, storagePool))
        {
            throw new InvalidOperationException("5至16路方案尚未通过当前机位、合成和全部录像盘位的60秒性能测试，请重新测试后再启用。");
        }
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim())).ToUpperInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim().ToUpperInvariant();
        }
    }

    private static void Append(StringBuilder target, object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
        target.Append('|').Append(text.Length).Append(':').Append(text);
    }
}
