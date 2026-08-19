using UnpackVision.App;
using UnpackVision.Core;
using UnpackVision.Infrastructure;

namespace UnpackVision.Tests;

public sealed class HikvisionChannelDiscoveryTests
{
    [Fact]
    public void ParsesPublishedMainAndSubStreamsByRecorderChannel()
    {
        var channels = HikvisionChannelDiscoveryParser.Parse("""
            <?xml version="1.0" encoding="UTF-8"?>
            <StreamingChannelList xmlns="http://www.hikvision.com/ver20/XMLSchema">
              <StreamingChannel>
                <id>101</id><enabled>true</enabled>
                <Video><videoCodecType>H.265</videoCodecType><videoResolutionWidth>2560</videoResolutionWidth><videoResolutionHeight>1440</videoResolutionHeight><maxFrameRate>2500</maxFrameRate></Video>
              </StreamingChannel>
              <StreamingChannel>
                <id>102</id><enabled>true</enabled>
                <Video><videoCodecType>H.264</videoCodecType><videoResolutionWidth>640</videoResolutionWidth><videoResolutionHeight>360</videoResolutionHeight><maxFrameRate>1500</maxFrameRate></Video>
              </StreamingChannel>
              <StreamingChannel>
                <id>201</id><enabled>true</enabled>
                <Video><videoCodecType>H.265</videoCodecType><videoResolutionWidth>1920</videoResolutionWidth><videoResolutionHeight>1080</videoResolutionHeight><maxFrameRate>25</maxFrameRate></Video>
              </StreamingChannel>
              <StreamingChannel><id>301</id><enabled>false</enabled></StreamingChannel>
              <StreamingChannel><id>403</id><enabled>true</enabled></StreamingChannel>
            </StreamingChannelList>
            """);

        Assert.Equal(2, channels.Count);
        Assert.Equal(1, channels[0].Channel);
        Assert.Equal("H.265", channels[0].MainStream?.Codec);
        Assert.Equal(25, channels[0].MainStream?.FramesPerSecond);
        Assert.Equal(15, channels[0].SubStream?.FramesPerSecond);
        Assert.Equal(2, channels[1].Channel);
        Assert.NotNull(channels[1].MainStream);
        Assert.Null(channels[1].SubStream);
    }

    [Fact]
    public void RejectsXmlDocumentTypeDefinitions()
    {
        Assert.ThrowsAny<System.Xml.XmlException>(() =>
            HikvisionChannelDiscoveryParser.Parse("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///tmp/private'>]><StreamingChannelList>&e;</StreamingChannelList>"));
    }

    [Fact]
    public void DiscoveryProfilesPreferSubStreamAndReuseRecorderCredentials()
    {
        var source = new CameraProfile
        {
            Id = "nvr-1",
            DisplayName = "NVR 1",
            SourceType = CameraSourceType.HikvisionRecorder,
            IsPrimary = true,
            HikvisionHost = "192.168.1.9",
            HikvisionHttpPort = 80,
            HikvisionRtspPort = 554,
            HikvisionChannel = 1,
            HikvisionSubStream = true,
            NetworkUsername = "local-user",
            NetworkPasswordProtected = "protected-value"
        };
        var profiles = new List<CameraProfile> { source };
        var channels = new[]
        {
            Channel(1),
            Channel(2)
        };

        var result = HikvisionChannelProfileMerger.Merge(profiles, source, channels, preferSubStream: true);

        Assert.Equal(1, result.AddedCount);
        Assert.Equal(1, result.ExistingCount);
        var added = Assert.Single(profiles, camera => camera.HikvisionChannel == 2);
        Assert.True(added.HikvisionSubStream);
        Assert.Equal(640, added.Width);
        Assert.Equal(360, added.Height);
        Assert.Equal(15, added.FramesPerSecond);
        Assert.Equal("protected-value", added.NetworkPasswordProtected);
    }

    [Fact]
    public void DiscoveryReportsChannelsThatExceedFourProfileLimit()
    {
        var source = new CameraProfile
        {
            SourceType = CameraSourceType.HikvisionRecorder,
            HikvisionHost = "192.168.1.9",
            HikvisionChannel = 1
        };
        var profiles = new List<CameraProfile>
        {
            source,
            new(),
            new(),
            new()
        };

        var result = HikvisionChannelProfileMerger.Merge(
            profiles,
            source,
            [Channel(1), Channel(2)],
            preferSubStream: true,
            maximumProfiles: 4);

        Assert.Equal([2], result.SkippedChannels);
        Assert.Equal(4, profiles.Count);
    }

    private static HikvisionChannelDescriptor Channel(int number) => new(
        number,
        new HikvisionStreamDescriptor(number * 100 + 1, "H.265", 2560, 1440, 25),
        new HikvisionStreamDescriptor(number * 100 + 2, "H.264", 640, 360, 15));
}
