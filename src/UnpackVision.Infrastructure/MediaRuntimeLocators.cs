using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

public sealed record MediaRuntimeExecutableCandidate(
    string ExecutablePath,
    MediaRuntimeOrigin Origin);

public sealed class GStreamerRuntimeLocator
{
    public const string InspectExecutableName = "gst-inspect-1.0.exe";
    public const string LaunchExecutableName = "gst-launch-1.0.exe";
    private readonly string _applicationBaseDirectory;
    private readonly Func<string, string?> _environmentReader;
    private readonly IReadOnlyList<MediaRuntimeExecutableCandidate>? _explicitCandidates;

    public GStreamerRuntimeLocator(
        string? applicationBaseDirectory = null,
        Func<string, string?>? environmentReader = null,
        IReadOnlyList<MediaRuntimeExecutableCandidate>? explicitCandidates = null)
    {
        _applicationBaseDirectory = Path.GetFullPath(applicationBaseDirectory ?? AppContext.BaseDirectory);
        _environmentReader = environmentReader ?? Environment.GetEnvironmentVariable;
        _explicitCandidates = explicitCandidates;
    }

    public IReadOnlyList<MediaRuntimeExecutableCandidate> LocateCandidates()
    {
        if (_explicitCandidates is not null)
        {
            return ExistingDistinct(_explicitCandidates);
        }

        var candidates = new List<MediaRuntimeExecutableCandidate>();
        AddBundledCandidates(candidates);
        AddEnvironmentRoot(candidates, "GSTREAMER_1_0_ROOT_MSVC_X86_64");
        AddEnvironmentRoot(candidates, "GSTREAMER_1_0_ROOT_X86_64");
        AddEnvironmentRoot(candidates, "GSTREAMER_ROOT_X86_64");

        var programFiles = _environmentReader("ProgramFiles");
        AddBinCandidate(
            candidates,
            CombineIfRooted(programFiles, "gstreamer", "1.0", "msvc_x86_64", "bin"),
            MediaRuntimeOrigin.System);

        foreach (var directory in SplitPath(_environmentReader("PATH")))
        {
            AddBinCandidate(candidates, directory, MediaRuntimeOrigin.Path);
        }

        return ExistingDistinct(candidates);
    }

    private void AddBundledCandidates(List<MediaRuntimeExecutableCandidate> candidates)
    {
        AddBinCandidate(candidates, Path.Combine(_applicationBaseDirectory, "GStreamer", "bin"), MediaRuntimeOrigin.Bundled);
        AddBinCandidate(candidates, Path.Combine(_applicationBaseDirectory, "GStreamer", "1.0", "msvc_x86_64", "bin"), MediaRuntimeOrigin.Bundled);
        AddBinCandidate(candidates, Path.Combine(_applicationBaseDirectory, "runtimes", "gstreamer", GStreamerRuntimeProbe.RequiredVersion, "bin"), MediaRuntimeOrigin.Bundled);

        foreach (var ancestor in EnumerateAncestors(_applicationBaseDirectory, maximumDepth: 6))
        {
            AddBinCandidate(candidates, Path.Combine(ancestor, "tools", "gstreamer", GStreamerRuntimeProbe.RequiredVersion, "bin"), MediaRuntimeOrigin.Bundled);
        }
    }

    private void AddEnvironmentRoot(List<MediaRuntimeExecutableCandidate> candidates, string variableName)
    {
        var value = _environmentReader(variableName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        AddBinCandidate(candidates, value, MediaRuntimeOrigin.Environment);
        AddBinCandidate(candidates, Path.Combine(value, "bin"), MediaRuntimeOrigin.Environment);
    }

    private static void AddBinCandidate(
        ICollection<MediaRuntimeExecutableCandidate> candidates,
        string? directory,
        MediaRuntimeOrigin origin)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            candidates.Add(new MediaRuntimeExecutableCandidate(
                Path.GetFullPath(Path.Combine(directory.Trim().Trim('"'), InspectExecutableName)),
                origin));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Ignore malformed environment entries instead of failing application startup.
        }
    }

    private static IReadOnlyList<MediaRuntimeExecutableCandidate> ExistingDistinct(
        IEnumerable<MediaRuntimeExecutableCandidate> candidates)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<MediaRuntimeExecutableCandidate>();
        foreach (var candidate in candidates)
        {
            try
            {
                var fullPath = Path.GetFullPath(candidate.ExecutablePath);
                if (!string.Equals(Path.GetFileName(fullPath), InspectExecutableName, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(fullPath) ||
                    !paths.Add(fullPath))
                {
                    continue;
                }
                result.Add(candidate with { ExecutablePath = fullPath });
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Skip one invalid candidate and continue probing trusted locations.
            }
        }
        return result;
    }

    private static IEnumerable<string> SplitPath(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? CombineIfRooted(string? root, params string[] segments)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }
        return segments.Aggregate(root, Path.Combine);
    }

    private static IEnumerable<string> EnumerateAncestors(string start, int maximumDepth)
    {
        var current = new DirectoryInfo(start);
        for (var depth = 0; current is not null && depth <= maximumDepth; depth++, current = current.Parent)
        {
            yield return current.FullName;
        }
    }
}

public sealed class FfmpegRuntimeLocator
{
    public const string ExecutableName = "ffmpeg.exe";
    private readonly string _applicationBaseDirectory;
    private readonly Func<string, string?> _environmentReader;
    private readonly IReadOnlyList<MediaRuntimeExecutableCandidate>? _explicitCandidates;

    public FfmpegRuntimeLocator(
        string? applicationBaseDirectory = null,
        Func<string, string?>? environmentReader = null,
        IReadOnlyList<MediaRuntimeExecutableCandidate>? explicitCandidates = null)
    {
        _applicationBaseDirectory = Path.GetFullPath(applicationBaseDirectory ?? AppContext.BaseDirectory);
        _environmentReader = environmentReader ?? Environment.GetEnvironmentVariable;
        _explicitCandidates = explicitCandidates;
    }

    public IReadOnlyList<MediaRuntimeExecutableCandidate> LocateCandidates()
    {
        if (_explicitCandidates is not null)
        {
            return ExistingDistinct(_explicitCandidates);
        }

        var candidates = new List<MediaRuntimeExecutableCandidate>();
        AddCandidate(candidates, Path.Combine(_applicationBaseDirectory, "FFmpeg", ExecutableName), MediaRuntimeOrigin.Bundled);
        AddCandidate(candidates, Path.Combine(_applicationBaseDirectory, "runtimes", "ffmpeg", FfmpegRuntimeProbe.ExpectedVersion, "bin", ExecutableName), MediaRuntimeOrigin.Bundled);
        foreach (var ancestor in EnumerateAncestors(_applicationBaseDirectory, maximumDepth: 6))
        {
            AddCandidate(candidates, Path.Combine(ancestor, "tools", "ffmpeg", FfmpegRuntimeProbe.ExpectedVersion, "bin", ExecutableName), MediaRuntimeOrigin.Bundled);
        }

        var configuredPath = _environmentReader("UNPACKVISION_FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            AddCandidate(candidates, configuredPath, MediaRuntimeOrigin.Environment);
        }
        foreach (var directory in SplitPath(_environmentReader("PATH")))
        {
            AddCandidate(candidates, Path.Combine(directory.Trim().Trim('"'), ExecutableName), MediaRuntimeOrigin.Path);
        }
        return ExistingDistinct(candidates);
    }

    private static void AddCandidate(
        ICollection<MediaRuntimeExecutableCandidate> candidates,
        string? executablePath,
        MediaRuntimeOrigin origin)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }
        try
        {
            candidates.Add(new MediaRuntimeExecutableCandidate(Path.GetFullPath(executablePath.Trim().Trim('"')), origin));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Ignore malformed environment entries.
        }
    }

    private static IReadOnlyList<MediaRuntimeExecutableCandidate> ExistingDistinct(
        IEnumerable<MediaRuntimeExecutableCandidate> candidates)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<MediaRuntimeExecutableCandidate>();
        foreach (var candidate in candidates)
        {
            try
            {
                var fullPath = Path.GetFullPath(candidate.ExecutablePath);
                if (!string.Equals(Path.GetFileName(fullPath), ExecutableName, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(fullPath) ||
                    !paths.Add(fullPath))
                {
                    continue;
                }
                result.Add(candidate with { ExecutablePath = fullPath });
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Skip one invalid candidate and continue.
            }
        }
        return result;
    }

    private static IEnumerable<string> SplitPath(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IEnumerable<string> EnumerateAncestors(string start, int maximumDepth)
    {
        var current = new DirectoryInfo(start);
        for (var depth = 0; current is not null && depth <= maximumDepth; depth++, current = current.Parent)
        {
            yield return current.FullName;
        }
    }
}
