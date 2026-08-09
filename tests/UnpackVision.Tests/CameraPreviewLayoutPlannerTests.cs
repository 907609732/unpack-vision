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
    public void BuildCreatesRequestedNumberOfPreviewSlots(int viewCount, int expected)
    {
        var plans = CameraPreviewLayoutPlanner.Build(viewCount, Cameras, []);

        Assert.Equal(expected, plans.Count);
        Assert.Equal(expected, plans.Select(plan => plan.Camera?.Id).Distinct().Count());
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
}
