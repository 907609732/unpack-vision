using UnpackVision.Core;
using UnpackVision.Infrastructure;

namespace UnpackVision.Tests;

public sealed class HikRecordingFileParserTests
{
    [Theory]
    [InlineData("SF5121858504349_20260310233439_20260310233537.mp4", "SF5121858504349", 58)]
    [InlineData("JD0225362965847-1-1-_20260310233927_20260310233940.mp4", "JD0225362965847-1-1-", 13)]
    [InlineData("TRACK_WITH_UNDERSCORE_20260310233927_20260310233940.mp4", "TRACK_WITH_UNDERSCORE", 13)]
    public void ParsesTrackingAndTimesFromRightSide(string fileName, string trackingNo, int seconds)
    {
        var parsed = HikRecordingFileParser.TryParse(fileName, WorkflowMode.Unpacking, out var recording);

        Assert.True(parsed);
        Assert.Equal(trackingNo, recording?.TrackingNo);
        Assert.Equal(seconds, (recording?.EndedAt - recording?.StartedAt)?.TotalSeconds);
    }

    [Theory]
    [InlineData("not-a-recording.mp4")]
    [InlineData("ABC_20260310233940_20260310233927.mp4")]
    [InlineData("ABC_20261310233927_20261310233940.mp4")]
    public void RejectsInvalidNames(string fileName) =>
        Assert.False(HikRecordingFileParser.TryParse(fileName, WorkflowMode.Unpacking, out _));
}
