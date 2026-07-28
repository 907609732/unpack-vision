using System.Text;
using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

public static class RecordingFileNameService
{
    private const int MaximumSafePathLength = 239;
    private const int MaximumIssueSuffixLength = 60;

    public static string BuildFileName(
        string trackingNo,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        IReadOnlyList<RecordTagAssignment> tags,
        int maximumFileNameLength = 220)
    {
        var prefix = $"{SanitizePart(trackingNo)}_{startedAt:yyyyMMddHHmmss}_{endedAt:yyyyMMddHHmmss}";
        var active = tags.Where(item => item.IsActive)
            .OrderBy(item => item.TaggedAt)
            .GroupBy(item => item.TagId, StringComparer.OrdinalIgnoreCase)
            .Select(group => SanitizePart(group.First().TagName))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();
        if (active.Length == 0)
        {
            return $"{prefix}.mp4";
        }

        var visible = new List<string>();
        for (var index = 0; index < active.Length; index++)
        {
            var trial = string.Join('-', visible.Append(active[index]));
            if (trial.Length > MaximumIssueSuffixLength)
            {
                break;
            }
            visible.Add(active[index]);
        }
        var hidden = active.Length - visible.Count;
        var issue = visible.Count == 0 ? "异常" : $"异常-{string.Join('-', visible)}";
        if (hidden > 0)
        {
            issue += $"-等{hidden}项";
        }
        var fileName = $"{prefix}_{issue}.mp4";
        if (fileName.Length <= maximumFileNameLength)
        {
            return fileName;
        }

        issue = active.Length > 1 ? $"异常-{active[0]}-等{active.Length - 1}项" : $"异常-{active[0]}";
        fileName = $"{prefix}_{issue}.mp4";
        if (fileName.Length <= maximumFileNameLength)
        {
            return fileName;
        }
        var minimal = $"{prefix}_异常.mp4";
        return minimal.Length <= maximumFileNameLength
            ? minimal
            : throw new PathTooLongException("完整单号、起止时间和异常标志超过安全文件名长度，请缩短录像目录。");
    }

    public static string GetAvailableFinalPath(
        string directory,
        string trackingNo,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        IReadOnlyList<RecordTagAssignment> tags,
        Guid recordId)
    {
        var fullDirectory = Path.GetFullPath(directory);
        var normalName = BuildFileName(trackingNo, startedAt, endedAt, []);
        if (fullDirectory.Length + 1 + normalName.Length > MaximumSafePathLength)
        {
            throw new PathTooLongException($"录像目录过深，完整单号和起止时间会使路径超过 {MaximumSafePathLength} 个字符，请在设置中选择更短的录像目录。");
        }
        var availableForName = MaximumSafePathLength - fullDirectory.Length - 1;
        var fileName = BuildFileName(trackingNo, startedAt, endedAt, tags, availableForName);
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }
        fileName = BuildFileName(trackingNo, startedAt, endedAt, tags, availableForName - 9);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        return Path.Combine(directory, $"{baseName}_{recordId.ToString("N")[..8]}.mp4");
    }

    public static string SanitizePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var builder = new StringBuilder(value.Normalize(NormalizationForm.FormC).Trim());
        for (var index = 0; index < builder.Length; index++)
        {
            if (invalid.Contains(builder[index]) || builder[index] is '/' or ':')
            {
                builder[index] = '-';
            }
        }
        var result = builder.ToString().Trim(' ', '.');
        return string.IsNullOrWhiteSpace(result) ? "unknown" : result;
    }
}
