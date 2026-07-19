using System.Globalization;
using System.Text.RegularExpressions;
using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

public static partial class HikRecordingFileParser
{
    [GeneratedRegex("^(?<tracking>.+)_(?<start>\\d{14})_(?<end>\\d{14})\\.mp4$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();

    public static bool TryParse(string path, WorkflowMode workflow, out ParsedRecording? recording)
    {
        recording = null;
        var match = FileNamePattern().Match(Path.GetFileName(path));
        if (!match.Success)
        {
            return false;
        }

        if (!DateTime.TryParseExact(
                match.Groups["start"].Value,
                "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var start) ||
            !DateTime.TryParseExact(
                match.Groups["end"].Value,
                "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var end) ||
            end < start)
        {
            return false;
        }

        recording = new ParsedRecording(
            match.Groups["tracking"].Value,
            workflow,
            new DateTimeOffset(start),
            new DateTimeOffset(end),
            Path.GetFullPath(path));
        return true;
    }
}
