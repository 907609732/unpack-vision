using System.Text.RegularExpressions;
using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

public sealed partial class GStreamerRuntimeProbe
{
    public const string RequiredVersion = "1.28.5";
    private static readonly TimeSpan VersionProbeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ElementProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan EncoderSmokeTestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OverallProbeTimeout = TimeSpan.FromSeconds(25);
    private static readonly string[] RequiredElementNames =
    [
        "mfvideosrc",
        "rtspsrc",
        "textoverlay",
        "mp4mux",
        "appsink",
        "fdsrc",
        "videoparse",
        "queue",
        "videoconvert",
        "h264parse",
        "filesink"
    ];
    private static readonly EncoderDefinition[] EncoderDefinitions =
    [
        new(MediaEncoderFamily.IntelQuickSync, "qsvh264enc", true, true),
        new(MediaEncoderFamily.NvidiaNvenc, "nvh264enc", true, true),
        new(MediaEncoderFamily.AmdAmf, "amfh264enc", true, true),
        new(MediaEncoderFamily.WindowsMediaFoundation, "mfh264enc", true, true),
        new(MediaEncoderFamily.Software, "openh264enc", false, true),
        new(MediaEncoderFamily.Software, "x264enc", false, false)
    ];

    private readonly GStreamerRuntimeLocator _locator;
    private readonly IExternalToolRunner _runner;

    public GStreamerRuntimeProbe(
        GStreamerRuntimeLocator? locator = null,
        IExternalToolRunner? runner = null)
    {
        _locator = locator ?? new GStreamerRuntimeLocator();
        _runner = runner ?? new ExternalToolRunner();
    }

    public async Task<GStreamerRuntimeCapabilities> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        using var timeout = new CancellationTokenSource(OverallProbeTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await ProbeCoreAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable("GStreamer capability inspection timed out.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Unavailable($"GStreamer capability inspection failed: {exception.GetType().Name}.");
        }
    }

    private async Task<GStreamerRuntimeCapabilities> ProbeCoreAsync(CancellationToken cancellationToken)
    {
        var candidates = _locator.LocateCandidates();
        if (candidates.Count == 0)
        {
            return Unavailable($"GStreamer {RequiredVersion} was not found.");
        }

        (MediaRuntimeExecutableCandidate Candidate, string Version)? incompatible = null;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var versionResult = await _runner.RunAsync(
                candidate.ExecutablePath,
                ["--version"],
                VersionProbeTimeout,
                cancellationToken);
            var version = TryReadVersion(versionResult);
            if (version is null)
            {
                continue;
            }

            if (!string.Equals(version, RequiredVersion, StringComparison.Ordinal))
            {
                incompatible ??= (candidate, version);
                continue;
            }

            return await InspectCompatibleRuntimeAsync(candidate, version, cancellationToken);
        }

        if (incompatible is { } found)
        {
            return new GStreamerRuntimeCapabilities(
                true,
                false,
                RequiredVersion,
                found.Version,
                found.Candidate.Origin,
                found.Candidate.ExecutablePath,
                FindSiblingLaunchExecutable(found.Candidate.ExecutablePath),
                RequiredElementNames.ToDictionary(name => name, _ => false, StringComparer.Ordinal),
                EncoderDefinitions.Select(definition => definition.Unavailable("Required GStreamer version is not active.")).ToArray(),
                $"GStreamer {found.Version} is installed, but version {RequiredVersion} is required.");
        }

        return Unavailable($"GStreamer {RequiredVersion} could not be started.");
    }

    private async Task<GStreamerRuntimeCapabilities> InspectCompatibleRuntimeAsync(
        MediaRuntimeExecutableCandidate candidate,
        string version,
        CancellationToken cancellationToken)
    {
        var elements = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var elementName in RequiredElementNames)
        {
            elements[elementName] = await ElementExistsAsync(
                candidate.ExecutablePath,
                elementName,
                cancellationToken);
        }

        var encoders = new List<MediaEncoderCapability>(EncoderDefinitions.Length);
        var launchExecutablePath = FindSiblingLaunchExecutable(candidate.ExecutablePath);
        var preferredHardwareSucceeded = false;
        foreach (var definition in EncoderDefinitions)
        {
            var registered = await ElementExistsAsync(
                candidate.ExecutablePath,
                definition.ElementName,
                cancellationToken);
            // Once a higher-priority approved hardware encoder has passed, probing slower
            // Media Foundation/software fallbacks adds startup delay and can hang inside a
            // vendor driver without changing the selected production encoder.
            var supersededByPreferredHardware = preferredHardwareSucceeded;
            var available = registered &&
                launchExecutablePath is not null &&
                !supersededByPreferredHardware &&
                await EncoderStartsAsync(
                    launchExecutablePath,
                    definition,
                    cancellationToken);
            if (available && definition.HardwareAccelerated && definition.ApprovedForBundledRedistribution)
            {
                preferredHardwareSucceeded = true;
            }
            var encoderMessage = available
                ? definition.ApprovedForBundledRedistribution
                    ? "Element registration and a bounded encode smoke test succeeded."
                    : "The encode smoke test succeeded locally, but this encoder is excluded from product redistribution."
                : !registered
                    ? "Element is not registered in the selected GStreamer runtime."
                    : supersededByPreferredHardware
                        ? "A higher-priority approved hardware encoder already passed; this fallback was not started."
                        : launchExecutablePath is null
                            ? "Element is registered, but gst-launch is unavailable for the required functional test."
                            : "Element is registered, but the bounded encode smoke test failed.";
            encoders.Add(new MediaEncoderCapability(
                definition.Family,
                definition.ElementName,
                available,
                definition.HardwareAccelerated,
                definition.ApprovedForBundledRedistribution,
                encoderMessage));
        }

        var missing = elements.Where(element => !element.Value).Select(element => element.Key).ToArray();
        var message = missing.Length == 0
            ? "Required GStreamer elements are available."
            : $"Missing required GStreamer elements: {string.Join(", ", missing)}.";
        return new GStreamerRuntimeCapabilities(
            true,
            true,
            RequiredVersion,
            version,
            candidate.Origin,
            candidate.ExecutablePath,
            launchExecutablePath,
            elements,
            encoders,
            message);
    }

    private async Task<bool> ElementExistsAsync(
        string inspectExecutablePath,
        string elementName,
        CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            inspectExecutablePath,
            ["--exists", elementName],
            ElementProbeTimeout,
            cancellationToken);
        return result.Started && !result.TimedOut && result.ExitCode == 0;
    }

    private async Task<bool> EncoderStartsAsync(
        string launchExecutablePath,
        EncoderDefinition definition,
        CancellationToken cancellationToken)
    {
        var rawFormat = definition.HardwareAccelerated ? "NV12" : "I420";
        var result = await _runner.RunAsync(
            launchExecutablePath,
            [
                "-q",
                "videotestsrc",
                "num-buffers=3",
                "pattern=black",
                "!",
                "videoconvert",
                "!",
                $"video/x-raw,format={rawFormat},width=320,height=240,framerate=5/1",
                "!",
                definition.ElementName,
                "!",
                "h264parse",
                "!",
                "mp4mux",
                "!",
                "fakesink",
                "sync=false"
            ],
            EncoderSmokeTestTimeout,
            cancellationToken);
        return result.Started && !result.TimedOut && result.ExitCode == 0;
    }

    private static string? TryReadVersion(ExternalToolResult result)
    {
        if (!result.Started || result.TimedOut || result.ExitCode != 0)
        {
            return null;
        }

        var match = GStreamerVersionRegex().Match(result.StandardOutput);
        if (!match.Success)
        {
            match = SemanticVersionRegex().Match(result.StandardOutput);
        }
        return match.Success ? match.Groups["version"].Value : null;
    }

    private static string? FindSiblingLaunchExecutable(string inspectExecutablePath)
    {
        var directory = Path.GetDirectoryName(inspectExecutablePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }
        var path = Path.Combine(directory, GStreamerRuntimeLocator.LaunchExecutableName);
        return File.Exists(path) ? path : null;
    }

    private static GStreamerRuntimeCapabilities Unavailable(string message) => new(
        false,
        false,
        RequiredVersion,
        null,
        MediaRuntimeOrigin.Unknown,
        null,
        null,
        RequiredElementNames.ToDictionary(name => name, _ => false, StringComparer.Ordinal),
        EncoderDefinitions.Select(definition => definition.Unavailable("GStreamer runtime is unavailable.")).ToArray(),
        message);

    [GeneratedRegex(@"(?im)^GStreamer\s+(?<version>\d+\.\d+\.\d+)(?:\.\d+)?\b", RegexOptions.CultureInvariant)]
    private static partial Regex GStreamerVersionRegex();

    [GeneratedRegex(@"\b(?<version>\d+\.\d+\.\d+)(?:\.\d+)?\b", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionRegex();

    private sealed record EncoderDefinition(
        MediaEncoderFamily Family,
        string ElementName,
        bool HardwareAccelerated,
        bool ApprovedForBundledRedistribution)
    {
        public MediaEncoderCapability Unavailable(string message) => new(
            Family,
            ElementName,
            false,
            HardwareAccelerated,
            ApprovedForBundledRedistribution,
            message);
    }
}
