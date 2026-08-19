using System.Security.Cryptography;
using System.Text.Json;
using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

/// <summary>
/// Repairs archives copied by releases that changed <c>RecordingRoot</c> but did not
/// persist a deletion receipt. A legacy report is only a discovery hint: this service
/// requires matching workspace manifests and re-runs the current copy/hash pipeline
/// before it is allowed to rewrite database paths or remove a source file.
/// </summary>
public sealed class LegacyRecordingMigrationRepairService(
    IScanRecordRepository repository,
    StorageOptions storageOptions)
{
    private const long MaximumReportBytes = 256 * 1024;
    private const long MaximumManifestBytes = 128 * 1024;
    private const int QueryPageSize = 200;

    public async Task<LegacyRecordingMigrationRepairCandidate?> FindPendingAsync(
        string currentRecordingRoot,
        CancellationToken cancellationToken = default)
    {
        var targetRoot = NormalizeRoot(currentRecordingRoot);
        var reports = await Task.Run(
            () => ReadLegacyReports(targetRoot, cancellationToken),
            cancellationToken);

        foreach (var report in reports.OrderByDescending(item => item.CompletedAt))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecordingRootMigrationPreview preview;
            try
            {
                preview = await BuildRepairPreviewAsync(report.SourceRoot, targetRoot, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }

            if (!preview.HasContent)
            {
                continue;
            }

            var affectedPaths = await CountPersistedPathsUnderAsync(report.SourceRoot, cancellationToken);
            return new LegacyRecordingMigrationRepairCandidate(
                report.ReportPath,
                report.SourceRoot,
                targetRoot,
                report.CompletedAt,
                preview.FileCount,
                preview.TotalBytes,
                affectedPaths);
        }

        return null;
    }

    public async Task<LegacyRecordingMigrationRepairResult> RepairAsync(
        LegacyRecordingMigrationRepairCandidate candidate,
        IProgress<RecordingRootMigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var targetRoot = NormalizeRoot(candidate.TargetRoot);
        var validated = ReadLegacyReports(targetRoot, cancellationToken)
            .SingleOrDefault(report =>
                string.Equals(report.ReportPath, Path.GetFullPath(candidate.ReportPath), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(report.SourceRoot, NormalizeRoot(candidate.SourceRoot), StringComparison.OrdinalIgnoreCase));
        if (validated is null)
        {
            throw new InvalidDataException("旧迁移报告已变化或不再可信，未执行清理。");
        }

        var migrationService = new RecordingRootMigrationService();
        var preview = await BuildRepairPreviewAsync(validated.SourceRoot, targetRoot, cancellationToken);
        if (!preview.HasContent)
        {
            throw new InvalidOperationException("旧录像目录已经没有可迁移文件，无需再次清理。");
        }

        // Existing destination files are reused only after a SHA-256 equality check. If a
        // destination is missing, the normal atomic migration path recreates it first.
        var migration = await migrationService.MigrateAsync(preview, progress, cancellationToken);
        await migrationService.VerifyAsync(migration, cancellationToken);

        // The SQLite backup precedes the one-transaction path rebase. Source cleanup is
        // deliberately last, so every database path is durable before deletion is possible.
        await new WorkspaceRecoveryService(repository, storageOptions)
            .BackupDatabaseForMigrationAsync(cancellationToken);
        var verifiedPathMap = migration.VerifiedFiles.ToDictionary(
            receipt => ResolveChild(migration.SourceRoot, receipt.RelativePath),
            receipt => ResolveChild(migration.TargetRoot, receipt.RelativePath),
            StringComparer.OrdinalIgnoreCase);
        var rebasedPaths = await repository.RebaseOwnedPathsAsync(verifiedPathMap, cancellationToken);
        var cleanup = await migrationService.CleanupSourceAsync(migration, progress, cancellationToken);

        return new LegacyRecordingMigrationRepairResult(migration, cleanup, rebasedPaths);
    }

    private static async Task<RecordingRootMigrationPreview> BuildRepairPreviewAsync(
        string sourceRoot,
        string targetRoot,
        CancellationToken cancellationToken)
    {
        var complete = await new RecordingRootMigrationService()
            .PreviewAsync(sourceRoot, targetRoot, cancellationToken);
        var paths = new List<string>(complete.RelativePaths.Count);
        long totalBytes = 0;
        foreach (var relativePath in complete.RelativePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsPortableMetadata(relativePath))
            {
                // The destination workspace and record sidecars can legitimately evolve after
                // the old copy-only migration. They are small and must not block reclaiming the
                // verified video/snapshot payload, nor may an older copy overwrite them.
                continue;
            }

            var sourcePath = ResolveChild(complete.SourceRoot, relativePath);
            var targetPath = ResolveChild(complete.TargetRoot, relativePath);
            if (File.Exists(targetPath) && !await AreFilesEqualAsync(sourcePath, targetPath, cancellationToken))
            {
                // A same-name destination with different content is never authorized for path
                // rebasing or source deletion. Leave it in place for manual reconciliation.
                continue;
            }

            paths.Add(relativePath);
            totalBytes = checked(totalBytes + new FileInfo(sourcePath).Length);
        }

        return new RecordingRootMigrationPreview(
            complete.SourceRoot,
            complete.TargetRoot,
            paths.Count,
            totalBytes,
            paths);
    }

    private static bool IsPortableMetadata(string relativePath)
    {
        var normalized = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normalized.StartsWith($".unpackvision{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> AreFilesEqualAsync(
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(sourcePath).Length != new FileInfo(targetPath).Length)
        {
            return false;
        }

        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var target = new FileStream(
            targetPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var sourceHash = await SHA256.HashDataAsync(source, cancellationToken);
        var targetHash = await SHA256.HashDataAsync(target, cancellationToken);
        return CryptographicOperations.FixedTimeEquals(sourceHash, targetHash);
    }

    private static IReadOnlyList<LegacyMigrationReport> ReadLegacyReports(
        string targetRoot,
        CancellationToken cancellationToken)
    {
        var reportsRoot = Path.Combine(targetRoot, ".unpackvision", "migration-reports");
        if (!Directory.Exists(reportsRoot))
        {
            return [];
        }

        var targetWorkspaceId = TryReadWorkspaceId(targetRoot);
        if (targetWorkspaceId is null)
        {
            return [];
        }

        var reports = new List<LegacyMigrationReport>();
        foreach (var path in Directory.EnumerateFiles(reportsRoot, "migration-*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsLegacyReportFileName(Path.GetFileName(path)))
            {
                continue;
            }

            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaximumReportBytes)
            {
                continue;
            }

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
                using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16
                });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    TryGetProperty(root, "VerifiedFiles", out _))
                {
                    // Reports created by the safe migration implementation already carry
                    // receipts and must never be treated as unsigned legacy reports.
                    continue;
                }

                if (!TryReadString(root, "SourceRoot", out var sourceValue) ||
                    !TryReadString(root, "TargetRoot", out var targetValue) ||
                    !TryReadDate(root, "CompletedAt", out var completedAt) ||
                    !TryReadNonNegativeInt(root, "TotalFiles", out var totalFiles) ||
                    !TryReadNonNegativeInt(root, "CopiedFiles", out var copiedFiles) ||
                    !TryReadNonNegativeInt(root, "VerifiedExistingFiles", out var verifiedExistingFiles) ||
                    totalFiles <= 0 || copiedFiles + verifiedExistingFiles != totalFiles)
                {
                    continue;
                }

                var sourceRoot = NormalizeRoot(sourceValue);
                var declaredTarget = NormalizeRoot(targetValue);
                if (!string.Equals(declaredTarget, targetRoot, StringComparison.OrdinalIgnoreCase) ||
                    !Directory.Exists(sourceRoot) ||
                    string.Equals(sourceRoot, targetRoot, StringComparison.OrdinalIgnoreCase) ||
                    IsChildOf(sourceRoot, targetRoot) ||
                    IsChildOf(targetRoot, sourceRoot))
                {
                    continue;
                }

                var sourceWorkspaceId = TryReadWorkspaceId(sourceRoot);
                if (sourceWorkspaceId is null || sourceWorkspaceId != targetWorkspaceId)
                {
                    continue;
                }

                reports.Add(new LegacyMigrationReport(
                    Path.GetFullPath(path),
                    sourceRoot,
                    targetRoot,
                    completedAt));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
            {
                // A malformed or inaccessible report is never a reason to broaden deletion.
            }
        }

        return reports;
    }

    private async Task<int> CountPersistedPathsUnderAsync(
        string sourceRoot,
        CancellationToken cancellationToken)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var offset = 0;; offset += QueryPageSize)
        {
            var records = await repository.QueryPageAsync(null, offset, QueryPageSize, cancellationToken);
            foreach (var record in records)
            {
                AddIfChild(paths, record.VideoPath, sourceRoot);
                foreach (var snapshot in record.Snapshots)
                {
                    AddIfChild(paths, snapshot, sourceRoot);
                }
                foreach (var asset in record.MediaAssets)
                {
                    AddIfChild(paths, asset.VideoPath, sourceRoot);
                    foreach (var segment in asset.Segments)
                    {
                        AddIfChild(paths, segment.VideoPath, sourceRoot);
                    }
                }
                foreach (var gap in record.MediaGaps)
                {
                    AddIfChild(paths, gap.RecoveryPath, sourceRoot);
                }
            }

            if (records.Count < QueryPageSize)
            {
                break;
            }
        }
        return paths.Count;
    }

    private static void AddIfChild(HashSet<string> paths, string? path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return;
        }
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (IsChildOf(fullPath, root))
            {
                paths.Add(fullPath);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
        }
    }

    private static Guid? TryReadWorkspaceId(string root)
    {
        var path = Path.Combine(root, ".unpackvision", "workspace.json");
        if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > MaximumManifestBytes)
        {
            return null;
        }
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 16 });
            return TryReadString(document.RootElement, "workspaceId", out var value) &&
                   Guid.TryParse(value, out var workspaceId) && workspaceId != Guid.Empty
                ? workspaceId
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static bool IsLegacyReportFileName(string fileName)
    {
        if (!fileName.StartsWith("migration-", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("migration-cleanup-", StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var stem = fileName["migration-".Length..^".json".Length];
        var parts = stem.Split('-');
        return parts.Length is 2 or 3 &&
               parts[0].Length == 8 && parts[0].All(char.IsAsciiDigit) &&
               parts[1].Length == 6 && parts[1].All(char.IsAsciiDigit) &&
               (parts.Length == 2 || parts[2].Length == 32 && parts[2].All(Uri.IsHexDigit));
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static bool TryReadString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!TryGetProperty(element, name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = property.GetString()?.Trim() ?? string.Empty;
        return value.Length is > 0 and <= 1024;
    }

    private static bool TryReadDate(JsonElement element, string name, out DateTimeOffset value)
    {
        value = default;
        return TryGetProperty(element, name, out var property) &&
               property.ValueKind == JsonValueKind.String &&
               property.TryGetDateTimeOffset(out value);
    }

    private static bool TryReadNonNegativeInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return TryGetProperty(element, name, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out value) && value >= 0;
    }

    private static string NormalizeRoot(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

    private static bool IsChildOf(string candidate, string root) =>
        candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string ResolveChild(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath) ||
            relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part == ".."))
        {
            throw new InvalidDataException("迁移收据包含越界路径。");
        }
        var normalizedRoot = NormalizeRoot(root);
        var fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        if (!IsChildOf(fullPath, normalizedRoot))
        {
            throw new InvalidDataException("迁移收据超出录像目录。");
        }
        return fullPath;
    }

    private sealed record LegacyMigrationReport(
        string ReportPath,
        string SourceRoot,
        string TargetRoot,
        DateTimeOffset CompletedAt);
}

public sealed record LegacyRecordingMigrationRepairCandidate(
    string ReportPath,
    string SourceRoot,
    string TargetRoot,
    DateTimeOffset OriginalMigrationCompletedAt,
    int FileCount,
    long TotalBytes,
    int PersistedSourcePathCount);

public sealed record LegacyRecordingMigrationRepairResult(
    RecordingRootMigrationResult Migration,
    RecordingRootMigrationCleanupResult Cleanup,
    int RebasedPaths);
