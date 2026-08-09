using System.Text.Json;

namespace UnpackVision.Infrastructure;

public sealed class UninstallCleanupPlan
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public bool DeleteBusinessData { get; set; }
    public string LocalDataRoot { get; set; } = string.Empty;
    public string RecordingRoot { get; set; } = string.Empty;
    public string ExcelWorkbookPath { get; set; } = string.Empty;
    public Guid WorkspaceId { get; set; }
}

public sealed record UninstallCleanupResult(bool PlanFound, bool Completed, IReadOnlyList<string> Errors);

/// <summary>
/// Applies an explicit, short-lived cleanup decision during Velopack's non-interactive
/// uninstall hook. A normal Windows uninstall has no plan and therefore preserves all
/// business data. Recording cleanup is limited to software-owned subdirectories under a
/// workspace whose ID still matches the user's confirmed settings.
/// </summary>
public static class UninstallCleanupService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly string[] OwnedRecordingDirectories =
        ["Unpacking", "Packing", "Snapshots", ".unpackvision"];

    public static string DefaultPlanPath => Path.Combine(
        Path.GetTempPath(),
        "UnpackVision",
        "pending-uninstall.json");

    public static string DefaultLocalDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UnpackVision");

    public static async Task PrepareAsync(
        LocalSettings settings,
        bool deleteBusinessData,
        string? planPath = null,
        string? localDataRoot = null,
        CancellationToken cancellationToken = default)
    {
        var targetPath = Path.GetFullPath(planPath ?? DefaultPlanPath);
        var plan = new UninstallCleanupPlan
        {
            DeleteBusinessData = deleteBusinessData,
            LocalDataRoot = Path.GetFullPath(localDataRoot ?? DefaultLocalDataRoot),
            RecordingRoot = Path.GetFullPath(settings.RecordingRoot),
            ExcelWorkbookPath = string.IsNullOrWhiteSpace(settings.ExcelWorkbookPath)
                ? string.Empty
                : Path.GetFullPath(settings.ExcelWorkbookPath),
            WorkspaceId = settings.Setup.WorkspaceId
        };

        if (deleteBusinessData)
        {
            ValidateLocalDataRoot(plan.LocalDataRoot, localDataRoot ?? DefaultLocalDataRoot);
            ValidateRecordingRoot(plan.RecordingRoot, plan.WorkspaceId);
        }

        var directory = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(
            temporary,
            JsonSerializer.Serialize(plan, JsonOptions),
            new System.Text.UTF8Encoding(false),
            cancellationToken);
        File.Move(temporary, targetPath, true);
    }

    public static UninstallCleanupResult ExecutePendingPlan(
        string? planPath = null,
        string? expectedLocalDataRoot = null)
    {
        var targetPath = Path.GetFullPath(planPath ?? DefaultPlanPath);
        if (!File.Exists(targetPath))
        {
            return new UninstallCleanupResult(false, true, []);
        }

        var errors = new List<string>();
        try
        {
            var plan = JsonSerializer.Deserialize<UninstallCleanupPlan>(File.ReadAllText(targetPath), JsonOptions)
                ?? throw new InvalidDataException("卸载清理计划无法读取");
            if (DateTimeOffset.Now - plan.CreatedAt > TimeSpan.FromMinutes(30) ||
                plan.CreatedAt - DateTimeOffset.Now > TimeSpan.FromMinutes(2))
            {
                throw new InvalidDataException("卸载清理计划已经过期");
            }
            if (!plan.DeleteBusinessData)
            {
                return new UninstallCleanupResult(true, true, []);
            }

            ValidateLocalDataRoot(plan.LocalDataRoot, expectedLocalDataRoot ?? DefaultLocalDataRoot);
            ValidateRecordingRoot(plan.RecordingRoot, plan.WorkspaceId);
            DeleteOwnedRecordingData(plan.RecordingRoot, plan.ExcelWorkbookPath, errors);
            TryDeleteDirectory(plan.LocalDataRoot, plan.ExcelWorkbookPath, errors);
            return new UninstallCleanupResult(true, errors.Count == 0, errors);
        }
        catch (Exception exception)
        {
            errors.Add(exception.Message);
            return new UninstallCleanupResult(true, false, errors);
        }
        finally
        {
            try
            {
                File.Delete(targetPath);
            }
            catch
            {
                // A stale plan must never authorize data deletion on a later uninstall.
            }
        }
    }

    public static void DiscardPendingPlan(string? planPath = null)
    {
        var targetPath = Path.GetFullPath(planPath ?? DefaultPlanPath);
        if (File.Exists(targetPath)) File.Delete(targetPath);
    }

    private static void ValidateLocalDataRoot(string actual, string expected)
    {
        if (!string.Equals(
                NormalizeDirectory(actual),
                NormalizeDirectory(expected),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("本机数据目录不是拆包智录的预期目录，已拒绝删除");
        }
    }

    private static void ValidateRecordingRoot(string recordingRoot, Guid workspaceId)
    {
        var root = NormalizeDirectory(recordingRoot);
        if (IsBroadSystemPath(root))
        {
            throw new InvalidDataException("录像目录范围过大，已拒绝删除");
        }

        var manifestPath = Path.Combine(root, ".unpackvision", "workspace.json");
        if (!File.Exists(manifestPath) || workspaceId == Guid.Empty)
        {
            throw new InvalidDataException("录像目录缺少有效的拆包智录工作区标记，已拒绝删除");
        }
        var manifest = JsonSerializer.Deserialize<UninstallWorkspaceIdentity>(File.ReadAllText(manifestPath), JsonOptions);
        if (manifest?.WorkspaceId != workspaceId)
        {
            throw new InvalidDataException("录像目录工作区与当前设置不匹配，已拒绝删除");
        }
    }

    private static bool IsBroadSystemPath(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.Equals(path, NormalizeDirectory(root ?? path), StringComparison.OrdinalIgnoreCase)) return true;

        var protectedPaths = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        };
        return protectedPaths
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeDirectory)
            .Any(value => string.Equals(value, path, StringComparison.OrdinalIgnoreCase));
    }

    private static void DeleteOwnedRecordingData(string recordingRoot, string excelWorkbookPath, List<string> errors)
    {
        var protectedFile = string.IsNullOrWhiteSpace(excelWorkbookPath)
            ? null
            : Path.GetFullPath(excelWorkbookPath);
        foreach (var directoryName in OwnedRecordingDirectories)
        {
            var target = Path.GetFullPath(Path.Combine(recordingRoot, directoryName));
            if (!IsChildOf(target, recordingRoot))
            {
                errors.Add($"录像子目录越界，已跳过：{directoryName}");
                continue;
            }
            TryDeleteDirectory(target, protectedFile, errors);
        }

        try
        {
            if (Directory.Exists(recordingRoot) && !Directory.EnumerateFileSystemEntries(recordingRoot).Any())
            {
                Directory.Delete(recordingRoot);
            }
        }
        catch (Exception exception)
        {
            errors.Add($"无法移除空录像目录：{exception.Message}");
        }
    }

    private static void TryDeleteDirectory(string directory, string? protectedFile, List<string> errors)
    {
        if (!Directory.Exists(directory)) return;
        try
        {
            DeleteTreePreserving(directory, protectedFile);
        }
        catch (Exception exception)
        {
            errors.Add($"无法删除 {directory}：{exception.Message}");
        }
    }

    private static bool DeleteTreePreserving(string directory, string? protectedFile)
    {
        var preserved = false;
        foreach (var childDirectory in Directory.EnumerateDirectories(directory))
        {
            preserved |= !DeleteTreePreserving(childDirectory, protectedFile);
        }
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (protectedFile is not null && string.Equals(Path.GetFullPath(file), protectedFile, StringComparison.OrdinalIgnoreCase))
            {
                preserved = true;
                continue;
            }
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }
        if (!preserved && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
            return true;
        }
        return false;
    }

    private static bool IsChildOf(string candidate, string parent)
    {
        var normalizedParent = NormalizeDirectory(parent) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDirectory(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private sealed class UninstallWorkspaceIdentity
    {
        public Guid WorkspaceId { get; set; }
    }
}
