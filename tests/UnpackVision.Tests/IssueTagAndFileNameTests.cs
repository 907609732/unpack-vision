using UnpackVision.Core;
using UnpackVision.Infrastructure;

namespace UnpackVision.Tests;

public sealed class IssueTagAndFileNameTests
{
    [Fact]
    public void IssueBarcodeIsMatchedBeforeTrackingNumber()
    {
        var tags = IssueTagDefaults.Create();

        var add = IssueTagBarcodeRouter.Match("UV-TAG-DAMAGE01\r\n", tags);
        var undo = IssueTagBarcodeRouter.Match("uv-undo-tag", tags);
        var tracking = IssueTagBarcodeRouter.Match("SF1234567890", tags);

        Assert.Equal(IssueBarcodeAction.AddTag, add.Action);
        Assert.Equal("破损", add.Tag?.Name);
        Assert.Equal(IssueBarcodeAction.UndoLastTag, undo.Action);
        Assert.Equal(IssueBarcodeAction.None, tracking.Action);
    }

    [Fact]
    public void BuildsNormalAndChronologicalIssueFileNames()
    {
        var start = new DateTimeOffset(2026, 7, 20, 10, 35, 12, TimeSpan.FromHours(8));
        var end = start.AddMinutes(5).AddSeconds(11);
        var normal = RecordingFileNameService.BuildFileName("690123456789", start, end, []);
        var abnormal = RecordingFileNameService.BuildFileName("690123456789", start, end,
        [
            new RecordTagAssignment { TagId = "SWAP", TagName = "调包", TaggedAt = start.AddMinutes(2) },
            new RecordTagAssignment { TagId = "DAMAGE", TagName = "破损/开裂", TaggedAt = start.AddMinutes(1) }
        ]);

        Assert.Equal("690123456789_20260720103512_20260720104023.mp4", normal);
        Assert.Equal("690123456789_20260720103512_20260720104023_异常-破损-开裂-调包.mp4", abnormal);
    }

    [Fact]
    public void RemovedTagsRestoreNormalFileNameAndManyTagsAreShortened()
    {
        var start = DateTimeOffset.Now;
        var removed = new RecordTagAssignment { TagId = "A", TagName = "破损", TaggedAt = start, RemovedAt = start.AddSeconds(1) };
        var restored = RecordingFileNameService.BuildFileName("TRACK123456", start, start.AddMinutes(1), [removed]);
        var many = Enumerable.Range(1, 20).Select(index => new RecordTagAssignment
        {
            TagId = index.ToString(),
            TagName = $"异常标签{index:00}",
            TaggedAt = start.AddSeconds(index)
        }).ToArray();
        var shortened = RecordingFileNameService.BuildFileName("TRACK123456", start, start.AddMinutes(1), many, 100);

        Assert.DoesNotContain("异常", restored);
        Assert.True(shortened.Length <= 100);
        Assert.Contains("等", shortened);
    }
}
