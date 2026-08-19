using UnpackVision.Core;
using UnpackVision.Core.Recording;
using UnpackVision.Infrastructure;
using UnpackVision.StationHost;
using System.Diagnostics;

namespace UnpackVision.Tests;

public sealed class StationHostStorageRootSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"UnpackVisionHostRoots-{Guid.NewGuid():N}");

    [Fact]
    public void TrustedRootsIncludeLegacyAndEnabledTargetsButExcludeDisabledTargets()
    {
        var legacy = Path.Combine(_root, "legacy");
        var second = Path.Combine(_root, "second");
        var disabled = Path.Combine(_root, "disabled");
        var options = CreateOptions(legacy, second, disabled);

        var roots = StationHostEndpointSupport.GetTrustedRecordingRoots(options);

        Assert.Contains(Path.GetFullPath(legacy), roots, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(Path.GetFullPath(second), roots, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFullPath(disabled), roots, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void MediaBoundaryAcceptsBothTrustedRootsAndRejectsTraversalSiblingAndDisabledTarget()
    {
        var legacy = Path.Combine(_root, "legacy");
        var second = Path.Combine(_root, "second");
        var disabled = Path.Combine(_root, "disabled");
        Directory.CreateDirectory(Path.Combine(legacy, "Unpacking"));
        Directory.CreateDirectory(Path.Combine(second, "Unpacking"));
        Directory.CreateDirectory(Path.Combine(disabled, "Unpacking"));
        File.WriteAllBytes(Path.Combine(legacy, "Unpacking", "one.mp4"), [1]);
        File.WriteAllBytes(Path.Combine(second, "Unpacking", "two.mp4"), [2]);
        File.WriteAllBytes(Path.Combine(disabled, "Unpacking", "three.mp4"), [3]);
        var options = CreateOptions(legacy, second, disabled);

        Assert.True(StationHostEndpointSupport.IsPathUnderTrustedRecordingRoots(
            Path.Combine(legacy, "Unpacking", "one.mp4"), options));
        Assert.True(StationHostEndpointSupport.IsPathUnderTrustedRecordingRoots(
            Path.Combine(second, "Unpacking", "two.mp4"), options));
        Assert.False(StationHostEndpointSupport.IsPathUnderTrustedRecordingRoots(
            Path.Combine(disabled, "Unpacking", "three.mp4"), options));
        Assert.False(StationHostEndpointSupport.IsPathUnderTrustedRecordingRoots(
            Path.Combine(legacy, "..", "outside.mp4"), options));
        Assert.False(StationHostEndpointSupport.IsPathUnderTrustedRecordingRoots(
            legacy + "-sibling" + Path.DirectorySeparatorChar + "outside.mp4", options));
    }

    [Fact]
    public void MediaBoundaryRejectsDirectoryJunctionThatEscapesTrustedRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var legacy = Path.Combine(_root, "legacy");
        var outside = Path.Combine(_root, "outside");
        var junction = Path.Combine(legacy, "linked-outside");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(outside);
        var outsideVideo = Path.Combine(outside, "private.mp4");
        var outsideSnapshot = Path.Combine(outside, "private.jpg");
        File.WriteAllBytes(outsideVideo, [1]);
        File.WriteAllBytes(outsideSnapshot, [2]);
        CreateDirectoryJunction(junction, outside);

        try
        {
            var options = CreateOptions(legacy, Path.Combine(_root, "second"), Path.Combine(_root, "disabled"));
            var linkedVideo = Path.Combine(junction, Path.GetFileName(outsideVideo));
            var linkedSnapshot = Path.Combine(junction, Path.GetFileName(outsideSnapshot));

            Assert.False(StationHostEndpointSupport.IsPathUnderTrustedRecordingRoots(linkedVideo, options));
            Assert.False(StationHostEndpointSupport.IsPathUnderTrustedRecordingRoots(linkedSnapshot, options));

            var now = DateTimeOffset.Now;
            var record = new ScanRecord
            {
                TrackingNo = "SAFE-JUNCTION",
                VideoPath = linkedVideo,
                Snapshots = [linkedSnapshot],
                ScannedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
                MediaAssets =
                [
                    new RecordMediaAsset
                    {
                        CameraId = "linked-outside",
                        VideoPath = linkedVideo
                    }
                ]
            };

            var view = StationHostEndpointSupport.ToStationRecordView(record, options);

            Assert.False(view.HasVideo);
            Assert.False(view.HasThumbnail);
            Assert.False(Assert.Single(view.MediaAssets!).HasVideo);
        }
        finally
        {
            if (Directory.Exists(junction))
            {
                Directory.Delete(junction);
            }
        }
    }

    [Fact]
    public void MediaBoundaryRejectsMissingMediaInsteadOfTrustingTextPrefix()
    {
        var legacy = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(legacy);
        var options = CreateOptions(legacy, Path.Combine(_root, "second"), Path.Combine(_root, "disabled"));

        Assert.False(StationHostEndpointSupport.IsPathUnderTrustedRecordingRoots(
            Path.Combine(legacy, "missing.mp4"), options));
    }

    [Fact]
    public void RecordProjectionDoesNotRevealFileExistenceOutsideTrustedRoots()
    {
        var legacy = Path.Combine(_root, "legacy");
        var second = Path.Combine(_root, "second");
        var disabled = Path.Combine(_root, "disabled");
        Directory.CreateDirectory(disabled);
        var outsideVideo = Path.Combine(disabled, "private.mp4");
        var outsideSnapshot = Path.Combine(disabled, "private.jpg");
        File.WriteAllBytes(outsideVideo, [1]);
        File.WriteAllBytes(outsideSnapshot, [2]);
        var options = CreateOptions(legacy, second, disabled);
        var record = new ScanRecord
        {
            TrackingNo = "SAFE-001",
            VideoPath = outsideVideo,
            Snapshots = [outsideSnapshot],
            ScannedAt = DateTimeOffset.Now,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
            MediaAssets =
            [
                new RecordMediaAsset
                {
                    CameraId = "outside",
                    VideoPath = outsideVideo
                }
            ]
        };

        var view = StationHostEndpointSupport.ToStationRecordView(record, options);

        Assert.False(view.HasVideo);
        Assert.False(view.HasThumbnail);
        Assert.False(Assert.Single(view.MediaAssets!).HasVideo);
    }

    private static StorageOptions CreateOptions(string legacy, string second, string disabled) => new()
    {
        RecordingRoot = legacy,
        StoragePool = new StoragePoolOptions
        {
            Targets =
            [
                new StorageTarget { Id = "legacy", RootPath = legacy, Enabled = true },
                new StorageTarget { Id = "second", RootPath = second, Enabled = true },
                new StorageTarget { Id = "disabled", RootPath = disabled, Enabled = false }
            ]
        }
    };

    private static void CreateDirectoryJunction(string junction, string target)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            ArgumentList =
            {
                "/d",
                "/c",
                "mklink",
                "/J",
                junction,
                target
            }
        });

        Assert.NotNull(process);
        Assert.True(process!.WaitForExit(10_000), "创建测试目录联接超时。");
        Assert.True(
            process.ExitCode == 0 && Directory.Exists(junction),
            $"无法创建测试目录联接：{process.StandardError.ReadToEnd()}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
        GC.SuppressFinalize(this);
    }
}
