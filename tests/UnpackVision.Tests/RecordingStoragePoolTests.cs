using UnpackVision.Core.Recording;
using UnpackVision.Infrastructure;

namespace UnpackVision.Tests;

public sealed class RecordingStoragePoolTests
{
    private const long GiB = StoragePoolOptions.Gibibyte;

    [Fact]
    public void Legacy_recording_root_becomes_first_target_without_a_camera_or_media_limit()
    {
        var options = StoragePoolOptions.FromLegacyRecordingRoot(@"D:\Recordings");

        var target = Assert.Single(options.Targets);
        Assert.Equal(StoragePoolOptions.LegacyTargetId, target.Id);
        Assert.Equal(@"D:\Recordings", target.RootPath);
        Assert.Equal(0, target.Priority);
        Assert.True(target.Enabled);
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(8, 13)]
    [InlineData(20, 25)]
    public void Safe_reserve_is_maximum_of_ten_gibibytes_and_projected_order_plus_five(
        long projectedGiB,
        long expectedGiB)
    {
        var options = new StoragePoolOptions();

        Assert.Equal(expectedGiB * GiB, options.CalculateSafeReserveBytes(projectedGiB * GiB));
    }

    [Fact]
    public async Task Monitor_has_no_artificial_target_limit()
    {
        var options = CreateOptions(Enumerable.Range(0, 20)
            .Select(index => CreateTarget($"disk-{index}", index))
            .ToArray());
        var monitor = new RecordingStoragePoolMonitor(new FakeDiskProbe(
            options.Targets.ToDictionary(
                target => target.Id,
                _ => Online(totalGiB: 100, availableGiB: 80))));

        var state = await monitor.InspectAsync(
            options,
            new StorageCapacityRequest(1 * GiB, 5 * GiB));

        Assert.Equal(20, state.Targets.Count);
        Assert.All(state.Targets, target => Assert.Equal(StorageTargetHealth.Ready, target.Health));
    }

    [Fact]
    public async Task Monitor_warns_below_fifteen_percent_even_when_safe_reserve_remains()
    {
        var target = CreateTarget("nearly-full", 0);
        var options = CreateOptions(target);
        var monitor = new RecordingStoragePoolMonitor(new FakeDiskProbe(new Dictionary<string, StorageDiskProbeResult>
        {
            [target.Id] = Online(totalGiB: 100, availableGiB: 14)
        }));

        var state = await monitor.InspectAsync(
            options,
            new StorageCapacityRequest(1 * GiB, 1 * GiB));

        var result = Assert.Single(state.Targets);
        Assert.Equal(StorageTargetHealth.Warning, result.Health);
        Assert.True(result.WarningReasons.HasFlag(StorageWarningReason.LowFreeSpacePercentage));
        Assert.False(result.WarningReasons.HasFlag(StorageWarningReason.LowEstimatedRecordingTime));
    }

    [Fact]
    public async Task Monitor_warns_when_projected_recording_time_is_below_two_hours()
    {
        var target = CreateTarget("fast-writer", 0);
        var options = CreateOptions(target);
        var monitor = new RecordingStoragePoolMonitor(new FakeDiskProbe(new Dictionary<string, StorageDiskProbeResult>
        {
            [target.Id] = Online(totalGiB: 100, availableGiB: 50)
        }));

        var state = await monitor.InspectAsync(
            options,
            new StorageCapacityRequest(1 * GiB, 30 * GiB));

        var result = Assert.Single(state.Targets);
        Assert.Equal(StorageTargetHealth.Warning, result.Health);
        Assert.True(result.WarningReasons.HasFlag(StorageWarningReason.LowEstimatedRecordingTime));
        Assert.InRange(result.EstimatedRecordingTime!.Value.TotalHours, 1.66, 1.67);
    }

    [Fact]
    public async Task Allocator_prefers_a_later_healthy_target_over_an_earlier_warning_target()
    {
        var warning = CreateTarget("warning", 0);
        var healthy = CreateTarget("healthy", 1);
        var options = CreateOptions(warning, healthy);
        var allocator = CreateAllocator(new Dictionary<string, StorageDiskProbeResult>
        {
            [warning.Id] = Online(totalGiB: 100, availableGiB: 14),
            [healthy.Id] = Online(totalGiB: 100, availableGiB: 70)
        });

        var result = await allocator.AllocateAsync(
            options,
            new StorageAllocationRequest("order-1", 1 * GiB, 1 * GiB));

        Assert.True(result.Succeeded);
        Assert.Equal(healthy.Id, result.Allocation!.TargetId);
        Assert.Equal(StorageWarningReason.None, result.Allocation.WarningReasons);
    }

    [Fact]
    public async Task Allocator_skips_offline_readonly_and_wrong_volume_targets()
    {
        var offline = CreateTarget("offline", 0);
        var readOnly = CreateTarget("readonly", 1);
        var wrongVolume = CreateTarget("wrong-volume", 2);
        wrongVolume.VolumeId = "expected-volume";
        var healthy = CreateTarget("healthy", 3);
        var options = CreateOptions(offline, readOnly, wrongVolume, healthy);
        var allocator = CreateAllocator(new Dictionary<string, StorageDiskProbeResult>
        {
            [offline.Id] = new(false, false, string.Empty, 0, 0, "offline"),
            [readOnly.Id] = new(true, false, "read-only-volume", 100 * GiB, 80 * GiB),
            [wrongVolume.Id] = new(true, true, "different-volume", 100 * GiB, 80 * GiB),
            [healthy.Id] = Online(totalGiB: 100, availableGiB: 80)
        });

        var result = await allocator.AllocateAsync(
            options,
            new StorageAllocationRequest("order-2", 1 * GiB, 1 * GiB));

        Assert.True(result.Succeeded);
        Assert.Equal(healthy.Id, result.Allocation!.TargetId);
        Assert.Equal(StorageTargetHealth.Offline, result.PoolState.Targets[0].Health);
        Assert.Equal(StorageTargetHealth.ReadOnly, result.PoolState.Targets[1].Health);
        Assert.Equal(StorageTargetHealth.VolumeMismatch, result.PoolState.Targets[2].Health);
    }

    [Fact]
    public async Task All_targets_below_safe_reserve_returns_clear_failure()
    {
        var first = CreateTarget("first", 0);
        var second = CreateTarget("second", 1);
        var options = CreateOptions(first, second);
        var allocator = CreateAllocator(new Dictionary<string, StorageDiskProbeResult>
        {
            [first.Id] = Online(totalGiB: 100, availableGiB: 9),
            [second.Id] = Online(totalGiB: 100, availableGiB: 7)
        });

        var result = await allocator.AllocateAsync(
            options,
            new StorageAllocationRequest("order-3", 1 * GiB, 1 * GiB));

        Assert.False(result.Succeeded);
        Assert.Null(result.Allocation);
        Assert.Equal(StorageAllocationFailureReason.InsufficientSpace, result.FailureReason);
        Assert.Contains("safe free-space reserve", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Concurrent_allocation_reserves_capacity_atomically_and_release_returns_it()
    {
        var target = CreateTarget("shared", 0);
        var options = CreateOptions(target);
        var allocator = CreateAllocator(new Dictionary<string, StorageDiskProbeResult>
        {
            [target.Id] = Online(totalGiB: 100, availableGiB: 21)
        });
        var request1 = new StorageAllocationRequest("order-a", 6 * GiB, 1 * GiB);
        var request2 = new StorageAllocationRequest("order-b", 6 * GiB, 1 * GiB);

        var allocations = await Task.WhenAll(
            allocator.AllocateAsync(options, request1),
            allocator.AllocateAsync(options, request2));

        Assert.All(allocations, result => Assert.True(result.Succeeded));
        var blocked = await allocator.AllocateAsync(
            options,
            new StorageAllocationRequest("order-c", 6 * GiB, 1 * GiB));
        Assert.Equal(StorageAllocationFailureReason.InsufficientSpace, blocked.FailureReason);

        await allocator.ReleaseAsync("order-a");
        var afterRelease = await allocator.AllocateAsync(
            options,
            new StorageAllocationRequest("order-c", 6 * GiB, 1 * GiB));
        Assert.True(afterRelease.Succeeded);
    }

    [Fact]
    public async Task Repeated_order_id_returns_the_same_allocation_without_double_reserving()
    {
        var target = CreateTarget("idempotent", 0);
        var options = CreateOptions(target);
        var allocator = CreateAllocator(new Dictionary<string, StorageDiskProbeResult>
        {
            [target.Id] = Online(totalGiB: 100, availableGiB: 21)
        });
        var request = new StorageAllocationRequest("same-order", 6 * GiB, 1 * GiB);

        var first = await allocator.AllocateAsync(options, request);
        var repeated = await allocator.AllocateAsync(options, request);
        var another = await allocator.AllocateAsync(
            options,
            new StorageAllocationRequest("another-order", 6 * GiB, 1 * GiB));

        Assert.Equal(first.Allocation, repeated.Allocation);
        Assert.True(another.Succeeded);
    }

    [Fact]
    public async Task System_probe_proves_write_access_without_leaving_probe_files()
    {
        var root = Path.Combine(Path.GetTempPath(), $"unpackvision-storage-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var result = await new SystemStorageDiskProbe().ProbeAsync(new StorageTarget
            {
                Id = "system-probe",
                RootPath = root
            });

            Assert.True(result.IsOnline);
            Assert.True(result.IsWritable);
            Assert.True(result.TotalBytes > 0);
            Assert.False(string.IsNullOrWhiteSpace(result.VolumeId));
            Assert.Empty(Directory.EnumerateFiles(root, ".unpackvision-write-probe-*.tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static StoragePoolOptions CreateOptions(params StorageTarget[] targets) => new()
    {
        Targets = [.. targets]
    };

    private static StorageTarget CreateTarget(string id, int priority) => new()
    {
        Id = id,
        DisplayName = id,
        RootPath = $@"D:\Recordings\{id}",
        Priority = priority,
        Enabled = true
    };

    private static RecordingStoragePoolAllocator CreateAllocator(
        IReadOnlyDictionary<string, StorageDiskProbeResult> results) =>
        new(new RecordingStoragePoolMonitor(new FakeDiskProbe(results)));

    private static StorageDiskProbeResult Online(long totalGiB, long availableGiB) =>
        new(true, true, "volume", totalGiB * GiB, availableGiB * GiB);

    private sealed class FakeDiskProbe(IReadOnlyDictionary<string, StorageDiskProbeResult> results)
        : IStorageDiskProbe
    {
        public ValueTask<StorageDiskProbeResult> ProbeAsync(
            StorageTarget target,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(results[target.Id]);
        }
    }
}
