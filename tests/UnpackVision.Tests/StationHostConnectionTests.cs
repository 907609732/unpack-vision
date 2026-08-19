using System.Text.Json;
using UnpackVision.App;
using UnpackVision.Core;

namespace UnpackVision.Tests;

public sealed class StationHostConnectionTests
{
    [Fact]
    public void LanAddressSetsMatch_IgnoresOrderWhitespaceAndDuplicates()
    {
        var matches = StationHostConnection.LanAddressSetsMatch(
            [" 192.168.31.100 ", "192.168.1.6", "192.168.1.6"],
            ["192.168.1.6", "192.168.31.100"]);

        Assert.True(matches);
    }

    [Fact]
    public void LanAddressSetsMatch_DetectsMissingListener()
    {
        var matches = StationHostConnection.LanAddressSetsMatch(
            ["192.168.1.6", "192.168.31.100"],
            ["192.168.1.6"]);

        Assert.False(matches);
    }

    [Fact]
    public void JsonOptions_ReadsStringRecordingStateFromStationHost()
    {
        const string json = """
            {
              "stationId": "TEST-STATION",
              "recordingState": "Idle",
              "recordId": null,
              "trackingNo": null,
              "serverTime": "2026-07-22T22:20:40+08:00",
              "mediaRelayRunning": false
            }
            """;

        var snapshot = JsonSerializer.Deserialize<StationStateSnapshot>(
            json,
            StationHostConnection.JsonOptions);

        Assert.NotNull(snapshot);
        Assert.Equal(RecordingState.Idle, snapshot.RecordingState);
        Assert.Equal("TEST-STATION", snapshot.StationId);
    }
}
