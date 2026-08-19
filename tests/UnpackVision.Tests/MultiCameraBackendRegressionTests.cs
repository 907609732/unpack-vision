using UnpackVision.Core;
using UnpackVision.Core.Recording;
using UnpackVision.Infrastructure;

namespace UnpackVision.Tests;

public sealed class MultiCameraBackendRegressionTests
{
    [Fact]
    public void Composite_default_is_enabled_through_four_routes_and_disabled_from_five()
    {
        Assert.True(CreateRig(1).EffectiveCompositeRecordingEnabled);
        Assert.True(CreateRig(4).EffectiveCompositeRecordingEnabled);
        Assert.False(CreateRig(5).EffectiveCompositeRecordingEnabled);
        Assert.False(CreateRig(16).EffectiveCompositeRecordingEnabled);

        var explicitFive = CreateRig(5);
        explicitFive.CompositeRecordingEnabled = true;
        Assert.True(explicitFive.EffectiveCompositeRecordingEnabled);

        var explicitFour = CreateRig(4);
        explicitFour.CompositeRecordingEnabled = false;
        Assert.False(explicitFour.EffectiveCompositeRecordingEnabled);
    }

    [Fact]
    public void Five_or_more_routes_require_exact_gstreamer_and_tested_hardware_encoder()
    {
        Assert.False(MultiCameraRecordingPolicy.RequiresProductionRuntime(4));
        Assert.True(MultiCameraRecordingPolicy.RequiresProductionRuntime(5));
        Assert.True(MultiCameraRecordingPolicy.RequiresProductionRuntime(16));

        var missing = Assert.Throws<InvalidOperationException>(() =>
            MultiCameraRecordingPolicy.RequireProductionRuntime(CreateRuntime(
                runtimeFound: false,
                versionMatches: false,
                encoder: null)));
        Assert.Contains("GStreamer 1.28.5", missing.Message, StringComparison.Ordinal);
        Assert.Contains("5至16路", missing.Message, StringComparison.Ordinal);

        var softwareOnly = Assert.Throws<InvalidOperationException>(() =>
            MultiCameraRecordingPolicy.RequireProductionRuntime(CreateRuntime(
                runtimeFound: true,
                versionMatches: true,
                new MediaEncoderCapability(
                    MediaEncoderFamily.Software,
                    "openh264enc",
                    Available: true,
                    HardwareAccelerated: false,
                    ApprovedForBundledRedistribution: true))));
        Assert.Contains("硬件H.264", softwareOnly.Message, StringComparison.Ordinal);

        var accepted = MultiCameraRecordingPolicy.RequireProductionRuntime(CreateRuntime(
            runtimeFound: true,
            versionMatches: true,
            new MediaEncoderCapability(
                MediaEncoderFamily.WindowsMediaFoundation,
                "mfh264enc",
                Available: true,
                HardwareAccelerated: true,
                ApprovedForBundledRedistribution: true)));
        Assert.Equal("gst-launch-1.0.exe", accepted.LaunchExecutablePath);
        Assert.Equal("mfh264enc", accepted.Encoder.ElementName);
    }

    [Fact]
    public async Task Backend_rejects_five_route_preview_before_opening_any_camera_when_runtime_is_missing()
    {
        var probe = new StaticMediaRuntimeProbe(CreateRuntime(
            runtimeFound: false,
            versionMatches: false,
            encoder: null));
        var root = Path.Combine(Path.GetTempPath(), $"unpackvision-runtime-{Guid.NewGuid():N}");
        var storagePool = StoragePoolOptions.FromLegacyRecordingRoot(root);
        var rig = CreateRig(5);
        rig.LastPerformanceTestPassed = true;
        rig.LastPerformanceTestFingerprint = CameraRigPerformanceAdmission.ComputeFingerprint(rig, storagePool);
        await using var backend = new MultiCameraRecordingBackend(
            new StorageOptions
            {
                RecordingRoot = root,
                StoragePool = storagePool
            },
            rig,
            mediaRuntimeProbe: probe);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => backend.StartPreviewAsync());

        Assert.Equal(1, probe.Calls);
        Assert.Contains("5至16路录像需要", error.Message, StringComparison.Ordinal);
        Assert.False(backend.IsPreviewing);
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 2, 1)]
    [InlineData(3, 2, 2)]
    [InlineData(4, 2, 2)]
    [InlineData(8, 4, 2)]
    [InlineData(16, 4, 4)]
    [InlineData(32, 6, 6)]
    public void Composite_grid_covers_supported_and_future_route_counts(
        int cameraCount,
        int expectedColumns,
        int expectedRows)
    {
        var (columns, rows) = MultiCameraRecordingPolicy.GetCompositeGridDimensions(cameraCount);

        Assert.Equal(expectedColumns, columns);
        Assert.Equal(expectedRows, rows);
        Assert.True(columns * rows >= cameraCount);
    }

    [Fact]
    public async Task Storage_lease_allocates_once_and_every_terminal_release_is_idempotent()
    {
        var allocation = CreateAllocation();
        var allocator = new CountingAllocator(allocation);
        var request = new StorageAllocationRequest(allocation.OrderId, 1024, 4096);

        var (result, lease) = await RecordingStorageLease.TryAcquireAsync(
            allocator,
            StoragePoolOptions.FromLegacyRecordingRoot(allocation.RootPath),
            request);

        Assert.True(result.Succeeded);
        Assert.NotNull(lease);
        Assert.Equal(1, allocator.AllocateCalls);

        // Stop, Abort and Dispose are all allowed to converge on the same terminal
        // operation without releasing a shared allocator reservation more than once.
        await lease!.ReleaseAsync();
        await lease.ReleaseAsync();
        await lease.DisposeAsync();

        Assert.Equal(1, allocator.ReleaseCalls);
        Assert.Equal(allocation.OrderId, allocator.ReleasedOrderId);
    }

    [Fact]
    public async Task Failed_storage_admission_does_not_create_a_releasable_lease()
    {
        var allocator = new CountingAllocator(allocation: null);

        var (result, lease) = await RecordingStorageLease.TryAcquireAsync(
            allocator,
            StoragePoolOptions.FromLegacyRecordingRoot(Path.GetTempPath()),
            new StorageAllocationRequest("blocked-order", 1024, 4096));

        Assert.False(result.Succeeded);
        Assert.Null(lease);
        Assert.Equal(1, allocator.AllocateCalls);
        Assert.Equal(0, allocator.ReleaseCalls);
    }

    [Fact]
    public void Media_asset_keeps_storage_encoder_watermark_relative_path_and_initial_segment()
    {
        var root = Path.Combine(Path.GetTempPath(), $"unpackvision-asset-{Guid.NewGuid():N}");
        var video = Path.Combine(root, "Unpacking", "TEST_20260809120000_20260809120500.mp4");
        var startedAt = new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.FromHours(8));
        var endedAt = startedAt.AddMinutes(5);
        var allocation = CreateAllocation(rootPath: root);

        var asset = MultiCameraMediaAssetFactory.Create(new RecordMediaAssetSpecification(
            Guid.NewGuid(),
            "camera-16",
            "仓库后侧",
            RecordMediaRole.Angle,
            video,
            allocation,
            "H.264",
            "mfh264enc",
            HardwareAccelerated: true,
            WatermarkBurnedIn: true,
            1920,
            1080,
            15,
            endedAt - startedAt,
            MediaIntegrityStatus.Complete,
            null,
            startedAt,
            endedAt));

        Assert.Equal(allocation.TargetId, asset.StorageTargetId);
        Assert.Equal("Unpacking/TEST_20260809120000_20260809120500.mp4", asset.RelativeVideoPath);
        Assert.Equal("H.264", asset.Codec);
        Assert.Equal("mfh264enc", asset.EncoderName);
        Assert.True(asset.HardwareAccelerated);
        Assert.True(asset.WatermarkBurnedIn);
        var segment = Assert.Single(asset.Segments);
        Assert.Equal(asset.Id, segment.MediaAssetId);
        Assert.Equal(asset.StorageTargetId, segment.StorageTargetId);
        Assert.Equal(asset.VideoPath, segment.VideoPath);
        Assert.Equal(TimeSpan.FromMinutes(5), segment.Duration);
    }

    [Fact]
    public void Failed_asset_has_no_completed_segment_and_path_cannot_escape_allocated_root()
    {
        var root = Path.Combine(Path.GetTempPath(), $"unpackvision-asset-{Guid.NewGuid():N}");
        var allocation = CreateAllocation(rootPath: root);
        var failed = CreateAssetSpecification(
            allocation,
            Path.Combine(root, "failed.mp4"),
            MediaIntegrityStatus.Failed);

        Assert.Empty(MultiCameraMediaAssetFactory.Create(failed).Segments);

        var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.mp4");
        Assert.Throws<InvalidOperationException>(() =>
            MultiCameraMediaAssetFactory.Create(CreateAssetSpecification(
                allocation,
                outside,
                MediaIntegrityStatus.Complete)));
    }

    [Fact]
    public async Task Independent_writer_group_waits_for_healthy_route_after_another_route_fails()
    {
        var healthyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowHealthyToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var healthyFinished = false;

        var group = IndependentWriterLoopGroup.Start(
        [
            async () =>
            {
                await healthyStarted.Task;
                throw new IOException("camera-2 encoder failed");
            },
            async () =>
            {
                healthyStarted.SetResult();
                await allowHealthyToFinish.Task;
                healthyFinished = true;
            }
        ]);

        await healthyStarted.Task;
        await Task.Delay(25);
        Assert.False(group.IsCompleted);
        allowHealthyToFinish.SetResult();

        var error = await Assert.ThrowsAsync<IOException>(() => group);
        Assert.Contains("camera-2", error.Message, StringComparison.Ordinal);
        Assert.True(healthyFinished);
    }

    [Fact]
    public async Task Writer_group_shutdown_has_a_hard_limit_and_forces_termination()
    {
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminated = false;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var completed = await IndependentWriterLoopGroup.WaitForShutdownAsync(
            never.Task,
            TimeSpan.FromMilliseconds(60),
            () => terminated = true,
            TimeSpan.FromMilliseconds(40));

        Assert.False(completed);
        Assert.True(terminated);
        Assert.InRange(stopwatch.ElapsedMilliseconds, 50, 1000);
    }

    [Fact]
    public void Failed_route_with_nonempty_partial_is_registered_as_recoverable_segment()
    {
        var root = Path.Combine(Path.GetTempPath(), $"unpackvision-partial-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var partial = Path.Combine(root, "secondary.partial.mp4");
        File.WriteAllBytes(partial, [1, 2, 3, 4]);
        try
        {
            Assert.True(MultiCameraMediaAssetFactory.HasRecoverablePartialFile(partial));
            var allocation = CreateAllocation(rootPath: root);
            var asset = MultiCameraMediaAssetFactory.Create(CreateAssetSpecification(
                allocation,
                partial,
                MediaIntegrityStatus.Partial));

            Assert.Equal(partial, asset.VideoPath);
            Assert.Equal(MediaIntegrityStatus.Partial, asset.Integrity);
            Assert.Equal(partial, Assert.Single(asset.Segments).VideoPath);
            Assert.False(MultiCameraMediaAssetFactory.HasRecoverablePartialFile(
                Path.Combine(root, "missing-final.mp4")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("侧面机位")]
    [InlineData("多机位合成")]
    public void Optional_media_move_failure_keeps_partial_and_creates_recoverable_segment(string displayName)
    {
        var root = Path.Combine(Path.GetTempPath(), $"unpackvision-finalize-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var temporary = Path.Combine(root, "angle.partial.mp4");
        var final = Path.Combine(root, "angle.mp4");
        File.WriteAllBytes(temporary, [1, 2, 3, 4]);
        File.WriteAllBytes(final, [9]);
        try
        {
            var result = MultiCameraMediaFinalizationPolicy.PromoteCompletedFile(
                temporary,
                final,
                required: false,
                displayName: displayName);

            Assert.False(result.PromotedToFinalPath);
            Assert.True(result.HasRecoverableMedia);
            Assert.Equal(MediaIntegrityStatus.Partial, result.Integrity);
            Assert.Equal(temporary, result.AssetPath);
            Assert.True(File.Exists(temporary));
            Assert.Contains("已保留可恢复临时文件", result.FailureReason, StringComparison.Ordinal);

            var asset = MultiCameraMediaAssetFactory.Create(CreateAssetSpecification(
                CreateAllocation(root),
                result.AssetPath,
                result.Integrity) with { FailureReason = result.FailureReason });
            Assert.Equal(temporary, Assert.Single(asset.Segments).VideoPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Primary_media_move_failure_fails_order_and_preserves_temporary_file()
    {
        var root = Path.Combine(Path.GetTempPath(), $"unpackvision-primary-finalize-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var temporary = Path.Combine(root, "primary.partial.mp4");
        var final = Path.Combine(root, "primary.mp4");
        File.WriteAllBytes(temporary, [1, 2, 3, 4]);
        File.WriteAllBytes(final, [9]);
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                MultiCameraMediaFinalizationPolicy.PromoteCompletedFile(
                    temporary,
                    final,
                    required: true,
                    displayName: "主机位"));

            Assert.Contains("主机位", error.Message, StringComparison.Ordinal);
            Assert.Contains("收尾失败", error.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(temporary));
            Assert.IsAssignableFrom<IOException>(error.InnerException);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Secondary_writer_failure_without_partial_is_failed_asset_gap_and_partial_order()
    {
        var root = Path.Combine(Path.GetTempPath(), $"unpackvision-missing-angle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var temporary = Path.Combine(root, "angle.partial.mp4");
            var final = Path.Combine(root, "angle.mp4");
            var result = MultiCameraMediaFinalizationPolicy.DescribeWriterFailure(
                temporary,
                final,
                "侧面机位",
                "副机位编码器启动失败");
            var startedAt = new DateTimeOffset(2026, 8, 10, 10, 0, 0, TimeSpan.FromHours(8));
            var endedAt = startedAt.AddMinutes(5);
            var recordId = Guid.NewGuid();
            var asset = MultiCameraMediaAssetFactory.Create(CreateAssetSpecification(
                CreateAllocation(root),
                result.AssetPath,
                result.Integrity) with
            {
                RecordId = recordId,
                CameraId = "camera-2",
                DisplayName = "侧面机位",
                Role = RecordMediaRole.Angle,
                FailureReason = result.FailureReason,
                StartedAt = startedAt,
                EndedAt = endedAt,
                Duration = endedAt - startedAt
            });
            var gap = MultiCameraMediaFinalizationPolicy.CreateMissingMediaGap(
                recordId,
                "camera-2",
                startedAt,
                endedAt,
                result.FailureReason!);

            Assert.False(result.HasRecoverableMedia);
            Assert.Equal(MediaIntegrityStatus.Failed, asset.Integrity);
            Assert.Empty(asset.Segments);
            Assert.Equal(startedAt, gap.StartedAt);
            Assert.Equal(endedAt, gap.EndedAt);
            Assert.False(gap.Recovered);
            Assert.Contains("未生成可恢复录像文件", gap.Reason, StringComparison.Ordinal);
            Assert.Equal(
                MediaIntegrityStatus.Partial,
                MultiCameraMediaFinalizationPolicy.DetermineOverallIntegrity([asset], [gap]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CameraRigOptions CreateRig(int count) => new()
    {
        Mode = CameraRigMode.MultiCamera,
        Cameras = Enumerable.Range(0, count)
            .Select(index => new CameraProfile
            {
                Id = $"camera-{index + 1}",
                DisplayName = $"机位 {index + 1}",
                Enabled = true,
                IsPrimary = index == 0,
                SortOrder = index
            })
            .ToList()
    };

    private static MediaRuntimeProbeResult CreateRuntime(
        bool runtimeFound,
        bool versionMatches,
        MediaEncoderCapability? encoder)
    {
        var requiredElements = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["mfvideosrc"] = true,
            ["rtspsrc"] = true,
            ["textoverlay"] = true,
            ["mp4mux"] = true,
            ["appsink"] = true
        };
        return new MediaRuntimeProbeResult(
            new GStreamerRuntimeCapabilities(
                runtimeFound,
                versionMatches,
                GStreamerRuntimeProbe.RequiredVersion,
                versionMatches ? GStreamerRuntimeProbe.RequiredVersion : null,
                MediaRuntimeOrigin.Bundled,
                runtimeFound ? "gst-inspect-1.0.exe" : null,
                runtimeFound ? "gst-launch-1.0.exe" : null,
                requiredElements,
                encoder is null ? [] : [encoder]),
            new FfmpegRuntimeCapabilities(
                false,
                false,
                "8.1.2",
                null,
                MediaRuntimeOrigin.Unknown,
                null,
                false,
                false,
                false));
    }

    private static StorageAllocation CreateAllocation(string? rootPath = null) => new(
        "order-1",
        "disk-a",
        "录像盘 A",
        rootPath ?? Path.Combine(Path.GetTempPath(), "unpackvision-recordings"),
        1024,
        10_000,
        StorageWarningReason.None,
        null);

    private static RecordMediaAssetSpecification CreateAssetSpecification(
        StorageAllocation allocation,
        string path,
        MediaIntegrityStatus integrity)
    {
        var startedAt = DateTimeOffset.UtcNow;
        return new RecordMediaAssetSpecification(
            Guid.NewGuid(),
            "camera-1",
            "主机位",
            RecordMediaRole.Primary,
            path,
            allocation,
            "H.264",
            "mfh264enc",
            true,
            true,
            1280,
            720,
            15,
            TimeSpan.FromMinutes(1),
            integrity,
            integrity == MediaIntegrityStatus.Failed ? "encoder failed" : null,
            startedAt,
            startedAt.AddMinutes(1));
    }

    private sealed class CountingAllocator(StorageAllocation? allocation) : IRecordingStorageAllocator
    {
        public int AllocateCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public string? ReleasedOrderId { get; private set; }

        public Task<StorageAllocationResult> AllocateAsync(
            StoragePoolOptions options,
            StorageAllocationRequest request,
            CancellationToken cancellationToken = default)
        {
            AllocateCalls++;
            return Task.FromResult(allocation is null
                ? StorageAllocationResult.Failure(
                    StorageAllocationFailureReason.InsufficientSpace,
                    "not enough space",
                    new StoragePoolState([]))
                : StorageAllocationResult.Success(allocation, new StoragePoolState([])));
        }

        public ValueTask ReleaseAsync(string orderId, CancellationToken cancellationToken = default)
        {
            ReleaseCalls++;
            ReleasedOrderId = orderId;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StaticMediaRuntimeProbe(MediaRuntimeProbeResult result) : IMediaRuntimeCapabilityProbe
    {
        public int Calls { get; private set; }

        public Task<MediaRuntimeProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }
}
