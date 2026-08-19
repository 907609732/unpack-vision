using UnpackVision.Core;
using UnpackVision.Core.Recording;
using UnpackVision.Infrastructure;

namespace UnpackVision.Tests;

public sealed class CameraRigPerformanceAdmissionTests
{
    [Fact]
    public void FingerprintInvalidatesWhenCompositeCameraOrAnyEnabledStorageTargetChanges()
    {
        var rig = CreateRig(5);
        var pool = CreatePool();
        rig.LastPerformanceTestPassed = true;
        rig.LastPerformanceTestFingerprint = CameraRigPerformanceAdmission.ComputeFingerprint(rig, pool);

        Assert.True(CameraRigPerformanceAdmission.IsCurrent(rig, pool));

        rig.CompositeRecordingEnabled = true;
        Assert.False(CameraRigPerformanceAdmission.IsCurrent(rig, pool));
        rig.CompositeRecordingEnabled = false;

        rig.Cameras[3].FramesPerSecond = 10;
        Assert.False(CameraRigPerformanceAdmission.IsCurrent(rig, pool));
        rig.Cameras[3].FramesPerSecond = 15;

        pool.Targets[1].RootPath = Path.Combine(Path.GetTempPath(), "uv-admission-c");
        Assert.False(CameraRigPerformanceAdmission.IsCurrent(rig, pool));
    }

    [Fact]
    public async Task HighChannelBackendRejectsMissingAdmissionBeforeOpeningCameras()
    {
        var root = Path.Combine(Path.GetTempPath(), $"uv-admission-{Guid.NewGuid():N}");
        var pool = new StoragePoolOptions
        {
            Targets = [new StorageTarget { Id = "primary", DisplayName = "录像盘", RootPath = root }]
        };
        var options = new StorageOptions { RecordingRoot = root, StoragePool = pool };
        await using var backend = new MultiCameraRecordingBackend(options, CreateRig(5));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => backend.StartPreviewAsync());

        Assert.Contains("60秒性能测试", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OneToFourCameraCompatibilityDoesNotRequireAdmission()
    {
        var rig = CreateRig(4);

        CameraRigPerformanceAdmission.EnsureHighChannelRigIsAdmitted(rig, CreatePool());
    }

    private static CameraRigOptions CreateRig(int count) => new()
    {
        Mode = CameraRigMode.MultiCamera,
        CompositeRecordingEnabled = false,
        Cameras = Enumerable.Range(0, count).Select(index => new CameraProfile
        {
            Id = $"camera-{index}",
            DisplayName = $"机位 {index + 1}",
            Enabled = true,
            IsPrimary = index == 0,
            SortOrder = index,
            Width = 1280,
            Height = 720,
            FramesPerSecond = 15
        }).ToList()
    };

    private static StoragePoolOptions CreatePool() => new()
    {
        Targets =
        [
            new StorageTarget { Id = "a", RootPath = Path.Combine(Path.GetTempPath(), "uv-admission-a"), Priority = 0 },
            new StorageTarget { Id = "b", RootPath = Path.Combine(Path.GetTempPath(), "uv-admission-b"), Priority = 1 }
        ]
    };
}
