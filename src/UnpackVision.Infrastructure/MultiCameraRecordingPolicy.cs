using UnpackVision.Core;
using UnpackVision.Core.Recording;

namespace UnpackVision.Infrastructure;

/// <summary>
/// Keeps high-channel admission, layout and task-isolation rules deterministic and
/// independently testable. Device discovery and process probing remain in their adapters.
/// </summary>
internal static class MultiCameraRecordingPolicy
{
    public static bool RequiresProductionRuntime(int cameraCount) =>
        cameraCount > CameraRigOptions.LegacyCompatibilityMaximumCameraCount;

    public static ProductionMediaRuntimeSelection RequireProductionRuntime(
        MediaRuntimeProbeResult capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var selection = new GStreamerH264EncoderSelector().Select(
            capabilities.GStreamer,
            requireHardwareAcceleration: true,
            requireBundledRedistributionApproval: true);
        var launchPath = capabilities.GStreamer.LaunchExecutablePath;
        if (!selection.Success || selection.Encoder is null || string.IsNullOrWhiteSpace(launchPath))
        {
            throw new InvalidOperationException(
                $"5至16路录像需要 GStreamer {GStreamerRuntimeProbe.RequiredVersion} 和通过实测的硬件H.264编码器。" +
                $" 当前检测：{selection.Message}");
        }

        return new ProductionMediaRuntimeSelection(launchPath, selection.Encoder);
    }

    public static (int Columns, int Rows) GetCompositeGridDimensions(int cameraCount)
    {
        if (cameraCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cameraCount));
        }
        if (cameraCount <= 1) return (1, 1);
        if (cameraCount <= 2) return (2, 1);
        if (cameraCount <= 4) return (2, 2);
        if (cameraCount <= 6) return (3, 2);
        if (cameraCount <= 8) return (4, 2);
        if (cameraCount <= 9) return (3, 3);
        if (cameraCount <= 12) return (4, 3);
        if (cameraCount <= 16) return (4, 4);

        var columns = (int)Math.Ceiling(Math.Sqrt(cameraCount));
        return (columns, (int)Math.Ceiling(cameraCount / (double)columns));
    }
}

internal sealed record ProductionMediaRuntimeSelection(
    string LaunchExecutablePath,
    MediaEncoderCapability Encoder);

/// <summary>
/// Owns exactly one allocator reservation. All terminal paths may safely call Release;
/// only the first call reaches the allocator, preventing reservation leaks and double release.
/// </summary>
internal sealed class RecordingStorageLease : IAsyncDisposable
{
    private IRecordingStorageAllocator? _allocator;

    private RecordingStorageLease(IRecordingStorageAllocator allocator, StorageAllocation allocation)
    {
        _allocator = allocator;
        Allocation = allocation;
    }

    public StorageAllocation Allocation { get; }

    public static async Task<(StorageAllocationResult Result, RecordingStorageLease? Lease)> TryAcquireAsync(
        IRecordingStorageAllocator allocator,
        StoragePoolOptions options,
        StorageAllocationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(allocator);
        var result = await allocator.AllocateAsync(options, request, cancellationToken).ConfigureAwait(false);
        return result.Succeeded && result.Allocation is { } allocation
            ? (result, new RecordingStorageLease(allocator, allocation))
            : (result, null);
    }

    public ValueTask ReleaseAsync(CancellationToken cancellationToken = default)
    {
        var allocator = Interlocked.Exchange(ref _allocator, null);
        return allocator is null
            ? ValueTask.CompletedTask
            : allocator.ReleaseAsync(Allocation.OrderId, cancellationToken);
    }

    public ValueTask DisposeAsync() => ReleaseAsync(CancellationToken.None);
}

/// <summary>
/// Starts each writer owner on an independent worker. Task.WhenAll observes failures only
/// after every route finishes and does not propagate one route's failure as cancellation.
/// </summary>
internal static class IndependentWriterLoopGroup
{
    public static Task Start(IEnumerable<Func<Task>> loops)
    {
        ArgumentNullException.ThrowIfNull(loops);
        var tasks = loops
            .Select(loop => Task.Run(loop, CancellationToken.None))
            .ToArray();
        return Task.WhenAll(tasks);
    }

    public static async Task<bool> WaitForShutdownAsync(
        Task writerLoop,
        TimeSpan timeout,
        Action forceTerminate,
        TimeSpan forceTerminationGrace)
    {
        ArgumentNullException.ThrowIfNull(writerLoop);
        ArgumentNullException.ThrowIfNull(forceTerminate);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (forceTerminationGrace < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(forceTerminationGrace));

        if (await Task.WhenAny(writerLoop, Task.Delay(timeout)).ConfigureAwait(false) == writerLoop)
        {
            await writerLoop.ConfigureAwait(false);
            return true;
        }

        forceTerminate();
        if (forceTerminationGrace > TimeSpan.Zero &&
            await Task.WhenAny(writerLoop, Task.Delay(forceTerminationGrace)).ConfigureAwait(false) == writerLoop)
        {
            try { await writerLoop.ConfigureAwait(false); } catch { }
        }
        else
        {
            _ = writerLoop.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        return false;
    }
}

internal sealed record RecordMediaAssetSpecification(
    Guid RecordId,
    string CameraId,
    string DisplayName,
    RecordMediaRole Role,
    string VideoPath,
    StorageAllocation StorageAllocation,
    string Codec,
    string EncoderName,
    bool HardwareAccelerated,
    bool WatermarkBurnedIn,
    int Width,
    int Height,
    double FramesPerSecond,
    TimeSpan Duration,
    MediaIntegrityStatus Integrity,
    string? FailureReason,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt);

internal sealed record MediaFileFinalizationResult(
    string AssetPath,
    MediaIntegrityStatus Integrity,
    string? FailureReason,
    bool HasRecoverableMedia,
    bool PromotedToFinalPath);

/// <summary>
/// Promotes an encoder-owned temporary file without allowing an optional camera to
/// invalidate an already completed primary recording. File promotion is deliberately
/// separated from writer finalization: a failed atomic move leaves the source in place
/// so recovery tooling can inspect or repair it later.
/// </summary>
internal static class MultiCameraMediaFinalizationPolicy
{
    public static MediaFileFinalizationResult PromoteCompletedFile(
        string temporaryPath,
        string finalPath,
        bool required,
        string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        try
        {
            if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0)
            {
                throw new InvalidDataException("编码器没有生成可用录像文件。");
            }
            File.Move(temporaryPath, finalPath, overwrite: false);
            return new MediaFileFinalizationResult(
                finalPath,
                MediaIntegrityStatus.Complete,
                null,
                HasRecoverableMedia: true,
                PromotedToFinalPath: true);
        }
        catch (Exception exception) when (IsFileFinalizationFailure(exception))
        {
            if (required)
            {
                throw new InvalidOperationException(
                    $"主机位“{displayName}”录像文件收尾失败，临时文件已保留。",
                    exception);
            }

            var recoverable = MultiCameraMediaAssetFactory.HasRecoverablePartialFile(temporaryPath);
            return new MediaFileFinalizationResult(
                recoverable ? temporaryPath : finalPath,
                recoverable ? MediaIntegrityStatus.Partial : MediaIntegrityStatus.Failed,
                recoverable
                    ? $"{displayName}录像文件收尾失败，已保留可恢复临时文件。"
                    : $"{displayName}录像文件收尾失败，且没有可恢复录像文件。",
                recoverable,
                PromotedToFinalPath: false);
        }
    }

    public static MediaFileFinalizationResult DescribeWriterFailure(
        string temporaryPath,
        string expectedFinalPath,
        string displayName,
        string? writerFailureReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedFinalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        var recoverable = MultiCameraMediaAssetFactory.HasRecoverablePartialFile(temporaryPath);
        var reason = string.IsNullOrWhiteSpace(writerFailureReason)
            ? $"{displayName}编码器失败"
            : writerFailureReason.Trim();
        return new MediaFileFinalizationResult(
            recoverable ? temporaryPath : expectedFinalPath,
            recoverable ? MediaIntegrityStatus.Partial : MediaIntegrityStatus.Failed,
            recoverable
                ? $"{reason}；已保留可恢复临时文件。"
                : $"{reason}；未生成可恢复录像文件。",
            recoverable,
            PromotedToFinalPath: false);
    }

    public static MediaGap CreateMissingMediaGap(
        Guid recordId,
        string cameraId,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        string reason) => new()
    {
        RecordId = recordId,
        CameraId = cameraId,
        StartedAt = startedAt,
        EndedAt = endedAt,
        Recovered = false,
        Reason = reason
    };

    public static MediaIntegrityStatus DetermineOverallIntegrity(
        IEnumerable<RecordMediaAsset> assets,
        IEnumerable<MediaGap> gaps)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(gaps);
        return assets.Any(asset => asset.Integrity != MediaIntegrityStatus.Complete) || gaps.Any()
            ? MediaIntegrityStatus.Partial
            : MediaIntegrityStatus.Complete;
    }

    private static bool IsFileFinalizationFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
}

/// <summary>
/// Creates the durable per-camera asset and its initial recoverable segment while enforcing
/// that the saved relative path cannot escape the storage target assigned to the order.
/// </summary>
internal static class MultiCameraMediaAssetFactory
{
    public static bool HasRecoverablePartialFile(string temporaryPath)
    {
        if (string.IsNullOrWhiteSpace(temporaryPath)) return false;
        try
        {
            return File.Exists(temporaryPath) && new FileInfo(temporaryPath).Length > 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static RecordMediaAsset Create(RecordMediaAssetSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        var relativePath = GetRelativePathWithinRoot(
            specification.StorageAllocation.RootPath,
            specification.VideoPath);
        var asset = new RecordMediaAsset
        {
            Id = Guid.NewGuid(),
            RecordId = specification.RecordId,
            CameraId = specification.CameraId,
            DisplayName = specification.DisplayName,
            Role = specification.Role,
            VideoPath = specification.VideoPath,
            StorageTargetId = specification.StorageAllocation.TargetId,
            RelativeVideoPath = relativePath,
            Codec = specification.Codec,
            EncoderName = specification.EncoderName,
            HardwareAccelerated = specification.HardwareAccelerated,
            WatermarkBurnedIn = specification.WatermarkBurnedIn,
            Width = specification.Width,
            Height = specification.Height,
            FramesPerSecond = specification.FramesPerSecond,
            Duration = specification.Duration,
            Integrity = specification.Integrity,
            FailureReason = specification.FailureReason,
            CreatedAt = specification.StartedAt,
            UpdatedAt = specification.EndedAt
        };
        if (specification.Integrity != MediaIntegrityStatus.Failed)
        {
            asset.Segments =
            [
                new RecordMediaSegment
                {
                    MediaAssetId = asset.Id,
                    Sequence = 0,
                    StorageTargetId = asset.StorageTargetId,
                    VideoPath = asset.VideoPath,
                    Duration = asset.Duration,
                    CreatedAt = specification.StartedAt
                }
            ];
        }
        return asset;
    }

    private static string GetRelativePathWithinRoot(string rootPath, string mediaPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(mediaPath);
        var rootWithSeparator = root + Path.DirectorySeparatorChar;
        if (!string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("媒体文件不在本单分配的录像盘位中。");
        }
        return Path.GetRelativePath(root, fullPath).Replace('\\', '/');
    }
}
