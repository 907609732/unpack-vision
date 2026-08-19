using UnpackVision.Core;

namespace UnpackVision.App;

internal sealed record HikvisionChannelMergeResult(
    int DiscoveredCount,
    int AddedCount,
    int ExistingCount,
    IReadOnlyList<int> SkippedChannels);

/// <summary>
/// Converts discovery results into editable camera profiles without saving settings. This keeps
/// device I/O out of the view event and lets the user review or cancel all changes in one dialog.
/// </summary>
internal static class HikvisionChannelProfileMerger
{
    public static HikvisionChannelMergeResult Merge(
        IList<CameraProfile> profiles,
        CameraProfile source,
        IReadOnlyList<HikvisionChannelDescriptor> channels,
        bool preferSubStream,
        int maximumProfiles = CameraRigOptions.MaximumEnabledCameras)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(channels);

        var added = 0;
        var existing = 0;
        var skipped = new List<int>();
        foreach (var channel in channels.Where(item => item.HasAnyStream).OrderBy(item => item.Channel))
        {
            var configured = profiles.FirstOrDefault(profile =>
                profile.SourceType == CameraSourceType.HikvisionRecorder &&
                string.Equals(profile.HikvisionHost.Trim(), source.HikvisionHost.Trim(), StringComparison.OrdinalIgnoreCase) &&
                profile.HikvisionRtspPort == source.HikvisionRtspPort &&
                profile.HikvisionChannel == channel.Channel);
            if (configured is not null)
            {
                CopySharedConnection(source, configured);
                EnsureAvailableStream(configured, channel, preferSubStream);
                existing++;
                continue;
            }

            if (profiles.Count >= maximumProfiles)
            {
                skipped.Add(channel.Channel);
                continue;
            }

            var (stream, useSubStream) = SelectStream(channel, preferSubStream);
            var profile = new CameraProfile
            {
                DisplayName = $"海康录像机 · 通道{channel.Channel}",
                Enabled = true,
                IsPrimary = profiles.Count == 0,
                SortOrder = profiles.Count,
                SourceType = CameraSourceType.HikvisionRecorder,
                AutoSelectBestCamera = false,
                Width = stream.Width > 0 ? stream.Width : source.Width,
                Height = stream.Height > 0 ? stream.Height : source.Height,
                FramesPerSecond = stream.FramesPerSecond > 0 ? stream.FramesPerSecond : source.FramesPerSecond,
                Codec = source.Codec,
                NetworkUsername = source.NetworkUsername,
                NetworkPasswordProtected = source.NetworkPasswordProtected,
                HikvisionHost = source.HikvisionHost,
                HikvisionHttpPort = source.HikvisionHttpPort,
                HikvisionRtspPort = source.HikvisionRtspPort,
                HikvisionChannel = channel.Channel,
                HikvisionSubStream = useSubStream
            };
            profiles.Add(profile);
            added++;
        }

        return new HikvisionChannelMergeResult(channels.Count, added, existing, skipped);
    }

    private static void CopySharedConnection(CameraProfile source, CameraProfile destination)
    {
        destination.Enabled = true;
        destination.NetworkUsername = source.NetworkUsername;
        destination.NetworkPasswordProtected = source.NetworkPasswordProtected;
        destination.HikvisionHost = source.HikvisionHost;
        destination.HikvisionHttpPort = source.HikvisionHttpPort;
        destination.HikvisionRtspPort = source.HikvisionRtspPort;
    }

    private static void EnsureAvailableStream(
        CameraProfile profile,
        HikvisionChannelDescriptor channel,
        bool preferSubStream)
    {
        var selectedIsAvailable = profile.HikvisionSubStream
            ? channel.SubStream is not null
            : channel.MainStream is not null;
        if (selectedIsAvailable)
        {
            return;
        }

        var (stream, useSubStream) = SelectStream(channel, preferSubStream);
        profile.HikvisionSubStream = useSubStream;
        if (stream.Width > 0) profile.Width = stream.Width;
        if (stream.Height > 0) profile.Height = stream.Height;
        if (stream.FramesPerSecond > 0) profile.FramesPerSecond = stream.FramesPerSecond;
    }

    private static (HikvisionStreamDescriptor Stream, bool UseSubStream) SelectStream(
        HikvisionChannelDescriptor channel,
        bool preferSubStream)
    {
        if (preferSubStream && channel.SubStream is not null)
        {
            return (channel.SubStream, true);
        }
        if (channel.MainStream is not null)
        {
            return (channel.MainStream, false);
        }
        if (channel.SubStream is not null)
        {
            return (channel.SubStream, true);
        }
        throw new ArgumentException($"通道 {channel.Channel} 没有可用码流", nameof(channel));
    }
}
