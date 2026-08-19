using System.Text.RegularExpressions;
using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

public sealed partial class FfmpegRuntimeProbe
{
    public const string ExpectedVersion = "8.1.2";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private readonly FfmpegRuntimeLocator _locator;
    private readonly IExternalToolRunner _runner;

    public FfmpegRuntimeProbe(
        FfmpegRuntimeLocator? locator = null,
        IExternalToolRunner? runner = null)
    {
        _locator = locator ?? new FfmpegRuntimeLocator();
        _runner = runner ?? new ExternalToolRunner();
    }

    public async Task<FfmpegRuntimeCapabilities> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (var candidate in _locator.LocateCandidates())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await _runner.RunAsync(
                    candidate.ExecutablePath,
                    ["-hide_banner", "-version"],
                    ProbeTimeout,
                    cancellationToken);
                if (!result.Started || result.TimedOut || result.ExitCode != 0)
                {
                    continue;
                }

                var version = TryReadVersion(result.StandardOutput);
                if (version is null)
                {
                    continue;
                }

                var gplEnabled = ContainsConfigurationFlag(result.StandardOutput, "--enable-gpl");
                var nonFreeEnabled = ContainsConfigurationFlag(result.StandardOutput, "--enable-nonfree");
                var approved = !gplEnabled && !nonFreeEnabled;
                var message = approved
                    ? "FFmpeg passes the product LGPL-only redistribution policy."
                    : nonFreeEnabled
                        ? "FFmpeg is available locally but nonfree components prohibit product redistribution."
                        : "FFmpeg is available locally but GPL components are excluded from product redistribution.";
                return new FfmpegRuntimeCapabilities(
                    true,
                    string.Equals(version, ExpectedVersion, StringComparison.Ordinal),
                    ExpectedVersion,
                    version,
                    candidate.Origin,
                    candidate.ExecutablePath,
                    gplEnabled,
                    nonFreeEnabled,
                    approved,
                    message);
            }
            return Unavailable("FFmpeg was not found or could not be started.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable("FFmpeg capability inspection timed out.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Unavailable($"FFmpeg capability inspection failed: {exception.GetType().Name}.");
        }
    }

    private static string? TryReadVersion(string output)
    {
        var match = FfmpegVersionRegex().Match(output);
        return match.Success ? match.Groups["version"].Value : null;
    }

    private static bool ContainsConfigurationFlag(string output, string flag) =>
        output.Contains(flag, StringComparison.OrdinalIgnoreCase);

    private static FfmpegRuntimeCapabilities Unavailable(string message) => new(
        false,
        false,
        ExpectedVersion,
        null,
        MediaRuntimeOrigin.Unknown,
        null,
        false,
        false,
        false,
        message);

    [GeneratedRegex(@"(?im)^ffmpeg version\s+(?:n-)?(?<version>\d+\.\d+(?:\.\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex FfmpegVersionRegex();
}

public sealed class MediaRuntimeCapabilityProbe : IMediaRuntimeCapabilityProbe
{
    private readonly GStreamerRuntimeProbe _gstreamer;
    private readonly FfmpegRuntimeProbe _ffmpeg;

    public MediaRuntimeCapabilityProbe(
        GStreamerRuntimeProbe? gstreamer = null,
        FfmpegRuntimeProbe? ffmpeg = null)
    {
        _gstreamer = gstreamer ?? new GStreamerRuntimeProbe();
        _ffmpeg = ffmpeg ?? new FfmpegRuntimeProbe();
    }

    public async Task<MediaRuntimeProbeResult> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        var gstreamerTask = _gstreamer.ProbeAsync(cancellationToken);
        var ffmpegTask = _ffmpeg.ProbeAsync(cancellationToken);
        await Task.WhenAll(gstreamerTask, ffmpegTask);
        return new MediaRuntimeProbeResult(
            await gstreamerTask,
            await ffmpegTask);
    }
}
