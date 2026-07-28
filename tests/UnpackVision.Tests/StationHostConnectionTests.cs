using System.Text.Json;
using UnpackVision.App;
using UnpackVision.Core;

namespace UnpackVision.Tests;

public sealed class StationHostConnectionTests
{
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
