using UnpackVision.App;
using UnpackVision.Core;

namespace UnpackVision.Tests;

public sealed class CameraPreviewLayoutPlannerTests
{
    private static readonly CameraProfile[] Cameras =
    [
        new() { Id = "primary", DisplayName = "主机位", Enabled = true, IsPrimary = true },
        new() { Id = "side", DisplayName = "侧面", Enabled = true },
        new() { Id = "back", DisplayName = "后方", Enabled = true },
        new() { Id = "nvr", DisplayName = "录像机", Enabled = true }
    ];

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    [InlineData(8, 8)]
    [InlineData(16, 16)]
    public void BuildCreatesRequestedNumberOfPreviewSlots(int viewCount, int expected)
    {
        var plans = CameraPreviewLayoutPlanner.Build(viewCount, CreateCameras(viewCount), []);

        Assert.Equal(expected, plans.Count);
        Assert.Equal(expected, plans.Select(plan => plan.Camera?.Id).Distinct().Count());
    }

    [Fact]
    public void SingleCameraModeUsesOneViewWithoutChangingTheSavedMultiCameraChoice()
    {
        const int savedMultiCameraViewCount = 4;

        Assert.Equal(1, CameraPreviewLayoutPlanner.ResolveViewCount(
            CameraRigMode.SingleCamera,
            savedMultiCameraViewCount));
        Assert.Equal(savedMultiCameraViewCount, CameraPreviewLayoutPlanner.ResolveViewCount(
            CameraRigMode.MultiCamera,
            savedMultiCameraViewCount));
    }

    [Fact]
    public void ThreeViewLayoutUsesLargeLeftAndTwoStackedRightTiles()
    {
        var plans = CameraPreviewLayoutPlanner.Build(3, Cameras, []);

        Assert.Equal((0, 0, 2, 1), (plans[0].Row, plans[0].Column, plans[0].RowSpan, plans[0].ColumnSpan));
        Assert.Equal((0, 1, 1, 1), (plans[1].Row, plans[1].Column, plans[1].RowSpan, plans[1].ColumnSpan));
        Assert.Equal((1, 1, 1, 1), (plans[2].Row, plans[2].Column, plans[2].RowSpan, plans[2].ColumnSpan));
    }

    [Fact]
    public void AssignSwapsExistingCameraInsteadOfDuplicatingIt()
    {
        var result = CameraPreviewLayoutPlanner.Assign(
            ["primary", "side", "back", "nvr"],
            4,
            0,
            "back");

        Assert.Equal(["back", "side", "primary", "nvr"], result);
        Assert.Equal(4, result.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void BuildKeepsEmptySlotsWhenFewerCamerasAreActive()
    {
        var plans = CameraPreviewLayoutPlanner.Build(4, Cameras.Take(2).ToArray(), []);

        Assert.Equal(4, plans.Count);
        Assert.Equal(2, plans.Count(plan => plan.Camera is not null));
        Assert.Equal(2, plans.Count(plan => plan.Camera is null));
    }

    [Fact]
    public void EightViewLayoutUsesFourByTwoGrid()
    {
        var plans = CameraPreviewLayoutPlanner.Build(8, CreateCameras(8), []);

        Assert.Equal((0, 0), (plans[0].Row, plans[0].Column));
        Assert.Equal((0, 3), (plans[3].Row, plans[3].Column));
        Assert.Equal((1, 0), (plans[4].Row, plans[4].Column));
        Assert.Equal((1, 3), (plans[7].Row, plans[7].Column));
    }

    [Fact]
    public void SixteenViewLayoutUsesFourByFourGrid()
    {
        var plans = CameraPreviewLayoutPlanner.Build(16, CreateCameras(16), []);

        Assert.Equal((0, 0), (plans[0].Row, plans[0].Column));
        Assert.Equal((3, 3), (plans[15].Row, plans[15].Column));
    }

    [Fact]
    public void LayoutAlgorithmCanScaleBeyondCurrentProductLimit()
    {
        var thirtyTwoPlans = CameraPreviewLayoutPlanner.Build(
            32,
            CreateCameras(32),
            [],
            maximumViewCount: 64);
        var sixtyFourPlans = CameraPreviewLayoutPlanner.Build(
            64,
            CreateCameras(64),
            [],
            maximumViewCount: 64);

        Assert.Equal(32, thirtyTwoPlans.Count);
        Assert.Equal((3, 7), (thirtyTwoPlans[31].Row, thirtyTwoPlans[31].Column));
        Assert.Equal(64, sixtyFourPlans.Count);
        Assert.Equal((7, 7), (sixtyFourPlans[63].Row, sixtyFourPlans[63].Column));
    }

    [Fact]
    public void DefaultLayoutLimitClampsRequestsToCurrentProductCapability()
    {
        var plans = CameraPreviewLayoutPlanner.Build(64, CreateCameras(64), []);

        Assert.Equal(CameraRigOptions.ProductMaximumCameraCount, plans.Count);
    }

    [Fact]
    public void MultiCameraRigExposesAllSixteenEnabledProfiles()
    {
        var rig = new CameraRigOptions
        {
            Mode = CameraRigMode.MultiCamera,
            Cameras = CreateCameras(20).ToList()
        };

        Assert.Equal(CameraRigOptions.ProductMaximumCameraCount, rig.EnabledCameras.Count);
        Assert.Equal("camera-15", rig.EnabledCameras[^1].Id);
    }

    [Fact]
    public void MediaCapabilitiesCannotExceedProductCameraLimit()
    {
        var capabilities = new MediaEngineCapabilities(
            MaximumTestedCameraCount: 64,
            MaximumSynchronizedPlaybackStreams: 16,
            AvailableHardwareEncoders: ["h264_qsv"],
            SupportsIsolatedCameraPipelines: true,
            SupportsRecoverableFragmentedMp4: true,
            SupportsCompositeRecording: true);

        Assert.Equal(CameraRigOptions.ProductMaximumCameraCount, capabilities.EffectiveMaximumCameraCount);
        Assert.Equal([4, 8, 16], CameraCapacityProfiles.BuiltIn.Select(profile => profile.MaximumCameraCount));

        var unavailable = capabilities with { MaximumTestedCameraCount = 0 };
        Assert.Equal(0, unavailable.EffectiveMaximumCameraCount);
    }

    private static CameraProfile[] CreateCameras(int count) => Enumerable.Range(0, count)
        .Select(index => new CameraProfile
        {
            Id = $"camera-{index}",
            DisplayName = $"机位 {index + 1}",
            Enabled = true,
            IsPrimary = index == 0,
            SortOrder = index
        })
        .ToArray();
}
