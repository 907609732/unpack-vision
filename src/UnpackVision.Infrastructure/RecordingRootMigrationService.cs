using System.Security.Cryptography;
using System.Text.Json;
using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

/// <summary>
/// Copies every selected file through a temporary name, verifies the complete archive,
/// and returns a receipt. The caller must persist the new recording root before invoking
/// source cleanup. Cleanup re-verifies both copies against the receipt before deletion.
/// </summary>
public sealed class RecordingRootMigrationService : IRecordingRootMigrationService
{
    private static readonly string[] VideoExtensions = [".mp4", ".avi", ".mov", ".mkv"];

    public Task<RecordingRootMigrationPreview> PreviewAsync(
        string sourceRoot,
        string targetRoot,
        CancellationToken cancellationToken = default)
    {
        var source = NormalizeRoot(sourceRoot, nameof(sourceRoot));
        var target = NormalizeRoot(targetRoot, nameof(targetRoot));
        EnsureSeparateRoots(source, target);

        if (!Directory.Exists(source))
        {
            return Task.FromResult(new RecordingRootMigrationPreview(source, target, 0, 0, []));
        }

        var paths = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Where(IsMigratableFile)
            .Select(path => Path.GetRelativePath(source, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        long totalBytes = 0;
        foreach (var relativePath in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalBytes = checked(totalBytes + new FileInfo(Path.Combine(source, relativePath)).Length);
        }

        return Task.FromResult(new RecordingRootMigrationPreview(source, target, paths.Length, totalBytes, paths));
    }

    public async Task<RecordingRootMigrationResult> MigrateAsync(
        RecordingRootMigrationPreview preview,
        IProgress<RecordingRootMigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var source = NormalizeRoot(preview.SourceRoot, nameof(preview.SourceRoot));
        var target = NormalizeRoot(preview.TargetRoot, nameof(preview.TargetRoot));
        EnsureSeparateRoots(source, target);

        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"旧录像目录不存在：{source}");
        }

        var available = new DriveInfo(Path.GetPathRoot(target)!).AvailableFreeSpace;
        if (available < preview.TotalBytes)
        {
            throw new IOException($"新位置可用空间不足。需要至少 {FormatBytes(preview.TotalBytes)}，当前只有 {FormatBytes(available)}。");
        }

        var copiedFiles = 0;
        var verifiedExistingFiles = 0;
        long copiedBytes = 0;
        long completedBytes = 0;
        Directory.CreateDirectory(target);
        for (var index = 0; index < preview.RelativePaths.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = preview.RelativePaths[index];
            var sourcePath = ResolveChild(source, relativePath);
            var targetPath = ResolveChild(target, relativePath);
            if (!File.Exists(sourcePath))
            {
                throw new IOException($"迁移期间找不到源文件：{relativePath}");
            }
            var sourceFileBytes = new FileInfo(sourcePath).Length;
            progress?.Report(new RecordingRootMigrationProgress(
                index,
                preview.FileCount,
                relativePath,
                completedBytes,
                preview.TotalBytes,
                0,
                sourceFileBytes));

            if (File.Exists(targetPath))
            {
                if (!await AreFilesEqualAsync(sourcePath, targetPath, cancellationToken))
                {
                    throw new IOException($"新位置存在同名但内容不同的文件：{relativePath}。请改用空目录后重试。");
                }
                verifiedExistingFiles++;
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                await CopyAtomicallyAsync(
                    sourcePath,
                    targetPath,
                    copiedForCurrentFile => progress?.Report(new RecordingRootMigrationProgress(
                        index,
                        preview.FileCount,
                        relativePath,
                        checked(completedBytes + copiedForCurrentFile),
                        preview.TotalBytes,
                        copiedForCurrentFile,
                        sourceFileBytes)),
                    cancellationToken);
                copiedFiles++;
                copiedBytes = checked(copiedBytes + sourceFileBytes);
            }

            completedBytes = checked(completedBytes + sourceFileBytes);
            progress?.Report(new RecordingRootMigrationProgress(
                index + 1,
                preview.FileCount,
                relativePath,
                completedBytes,
                preview.TotalBytes,
                sourceFileBytes,
                sourceFileBytes));
        }

        // This second pass is intentional. A large migration may have copied its first
        // file minutes ago; settings must not switch until the complete target still
        // matches the complete source at one well-defined verification boundary.
        var verifiedFiles = new List<RecordingRootMigrationFileReceipt>(preview.FileCount);
        foreach (var relativePath in preview.RelativePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = ResolveChild(source, relativePath);
            var targetPath = ResolveChild(target, relativePath);
            if (!File.Exists(sourcePath) || !File.Exists(targetPath))
            {
                throw new IOException($"最终校验时文件缺失：{relativePath}");
            }

            var receipt = await CreateReceiptAsync(relativePath, sourcePath, targetPath, cancellationToken);
            if (receipt is null)
            {
                throw new IOException($"最终校验失败，源文件和目标文件不一致：{relativePath}");
            }
            verifiedFiles.Add(receipt);
        }

        var reportPath = await WriteReportAsync(
            target,
            new RecordingRootMigrationReport(
                DateTimeOffset.Now,
                source,
                target,
                preview.FileCount,
                copiedFiles,
                verifiedExistingFiles,
                copiedBytes,
                verifiedFiles),
            cancellationToken);
        return new RecordingRootMigrationResult(
            copiedFiles,
            verifiedExistingFiles,
            copiedBytes,
            reportPath,
            source,
            target,
            verifiedFiles);
    }

    public async Task VerifyAsync(
        RecordingRootMigrationResult migration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(migration);
        var source = NormalizeRoot(migration.SourceRoot, nameof(migration.SourceRoot));
        var target = NormalizeRoot(migration.TargetRoot, nameof(migration.TargetRoot));
        EnsureSeparateRoots(source, target);
        foreach (var receipt in migration.VerifiedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = ResolveChild(source, receipt.RelativePath);
            var targetPath = ResolveChild(target, receipt.RelativePath);
            if (!File.Exists(sourcePath) || !File.Exists(targetPath))
            {
                throw new IOException($"提交迁移前复核发现文件缺失：{receipt.RelativePath}");
            }
            var current = await CreateReceiptAsync(
                receipt.RelativePath,
                sourcePath,
                targetPath,
                cancellationToken);
            if (current is null || current.Length != receipt.Length ||
                !string.Equals(current.Sha256, receipt.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"提交迁移前复核失败，源文件或目标文件已变化：{receipt.RelativePath}");
            }
        }
    }

    public async Task<RecordingRootMigrationCleanupResult> CleanupSourceAsync(
        RecordingRootMigrationResult migration,
        IProgress<RecordingRootMigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(migration);
        var source = NormalizeRoot(migration.SourceRoot, nameof(migration.SourceRoot));
        var target = NormalizeRoot(migration.TargetRoot, nameof(migration.TargetRoot));
        EnsureSeparateRoots(source, target);

        var failures = new List<RecordingRootMigrationCleanupFailure>();
        var deletedFiles = 0;
        long deletedBytes = 0;
        long completedBytes = 0;
        var totalBytes = migration.VerifiedFiles.Sum(file => file.Length);
        for (var index = 0; index < migration.VerifiedFiles.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var receipt = migration.VerifiedFiles[index];
            progress?.Report(new RecordingRootMigrationProgress(
                index,
                migration.VerifiedFiles.Count,
                receipt.RelativePath,
                completedBytes,
                totalBytes,
                0,
                receipt.Length));
            try
            {
                string sourcePath;
                string targetPath;
                try
                {
                    sourcePath = ResolveChild(source, receipt.RelativePath);
                    targetPath = ResolveChild(target, receipt.RelativePath);
                }
                catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
                {
                    failures.Add(new RecordingRootMigrationCleanupFailure(receipt.RelativePath, exception.Message));
                    continue;
                }

                if (!File.Exists(sourcePath))
                {
                    continue;
                }
                if (!IsMigratableFile(sourcePath))
                {
                    failures.Add(new RecordingRootMigrationCleanupFailure(receipt.RelativePath, "文件类型不属于录像迁移范围"));
                    continue;
                }
                if (!File.Exists(targetPath))
                {
                    failures.Add(new RecordingRootMigrationCleanupFailure(receipt.RelativePath, "目标文件缺失，已保留源文件"));
                    continue;
                }

                try
                {
                    if (!await DeleteSourceIfReceiptMatchesAsync(sourcePath, targetPath, receipt, cancellationToken))
                    {
                        failures.Add(new RecordingRootMigrationCleanupFailure(
                            receipt.RelativePath,
                            "源文件或目标文件在迁移后发生变化，已保留源文件"));
                        continue;
                    }
                    deletedFiles++;
                    deletedBytes = checked(deletedBytes + receipt.Length);
                    TryDeleteEmptyParents(Path.GetDirectoryName(sourcePath), source);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    failures.Add(new RecordingRootMigrationCleanupFailure(receipt.RelativePath, exception.Message));
                }
            }
            finally
            {
                completedBytes = checked(completedBytes + receipt.Length);
                progress?.Report(new RecordingRootMigrationProgress(
                    index + 1,
                    migration.VerifiedFiles.Count,
                    receipt.RelativePath,
                    completedBytes,
                    totalBytes,
                    receipt.Length,
                    receipt.Length));
            }
        }

        var cleanupReportPath = await WriteCleanupReportAsync(
            target,
            new RecordingRootMigrationCleanupReport(
                DateTimeOffset.Now,
                migration.ReportPath,
                source,
                target,
                deletedFiles,
                deletedBytes,
                failures),
            cancellationToken);
        return new RecordingRootMigrationCleanupResult(
            deletedFiles,
            deletedBytes,
            failures,
            cleanupReportPath);
    }

    /// <summary>
    /// Re-runs a previously issued cleanup receipt. The receipt never authorizes deleting
    /// a path whose source and destination no longer match its SHA-256 and length.
    /// </summary>
    public Task<RecordingRootMigrationCleanupResult> RetryCleanupAsync(
        RecordingRootMigrationResult migration,
        IProgress<RecordingRootMigrationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        CleanupSourceAsync(migration, progress, cancellationToken);

    private static bool IsMigratableFile(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".partial.mp4", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var isPortableIndex = normalized.Contains(
            $"{Path.DirectorySeparatorChar}.unpackvision{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase);
        var isSnapshot = normalized.Contains(
            $"{Path.DirectorySeparatorChar}Snapshots{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase);
        return isPortableIndex || isSnapshot || VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task CopyAtomicallyAsync(
        string sourcePath,
        string targetPath,
        Action<long>? copiedBytesChanged,
        CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(targetPath)!,
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.migration.tmp");
        try
        {
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var target = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var buffer = new byte[1024 * 1024];
                long copiedBytes = 0;
                int bytesRead;
                while ((bytesRead = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    copiedBytes = checked(copiedBytes + bytesRead);
                    copiedBytesChanged?.Invoke(copiedBytes);
                }
                await target.FlushAsync(cancellationToken);
            }

            if (!await AreFilesEqualAsync(sourcePath, temporaryPath, cancellationToken))
            {
                throw new IOException($"复制校验失败：{Path.GetFileName(sourcePath)}");
            }
            File.Move(temporaryPath, targetPath, false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task<bool> AreFilesEqualAsync(string left, string right, CancellationToken cancellationToken)
    {
        if (new FileInfo(left).Length != new FileInfo(right).Length)
        {
            return false;
        }
        await using var leftStream = File.OpenRead(left);
        await using var rightStream = File.OpenRead(right);
        var leftHash = await SHA256.HashDataAsync(leftStream, cancellationToken);
        var rightHash = await SHA256.HashDataAsync(rightStream, cancellationToken);
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }

    private static async Task<RecordingRootMigrationFileReceipt?> CreateReceiptAsync(
        string relativePath,
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        var sourceLength = new FileInfo(sourcePath).Length;
        if (sourceLength != new FileInfo(targetPath).Length)
        {
            return null;
        }

        await using var sourceStream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var targetStream = new FileStream(
            targetPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var sourceHash = await SHA256.HashDataAsync(sourceStream, cancellationToken);
        var targetHash = await SHA256.HashDataAsync(targetStream, cancellationToken);
        return CryptographicOperations.FixedTimeEquals(sourceHash, targetHash)
            ? new RecordingRootMigrationFileReceipt(relativePath, sourceLength, Convert.ToHexString(sourceHash))
            : null;
    }

    private static async Task<bool> DeleteSourceIfReceiptMatchesAsync(
        string sourcePath,
        string targetPath,
        RecordingRootMigrationFileReceipt receipt,
        CancellationToken cancellationToken)
    {
        if (receipt.Length < 0 || receipt.Sha256.Length != SHA256.HashSizeInBytes * 2)
        {
            return false;
        }

        byte[] expectedHash;
        try
        {
            expectedHash = Convert.FromHexString(receipt.Sha256);
        }
        catch (FormatException)
        {
            return false;
        }
        await using var sourceStream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var targetStream = new FileStream(
            targetPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (sourceStream.Length != receipt.Length || targetStream.Length != receipt.Length)
        {
            return false;
        }

        var sourceHash = await SHA256.HashDataAsync(sourceStream, cancellationToken);
        var targetHash = await SHA256.HashDataAsync(targetStream, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(sourceHash, expectedHash) ||
            !CryptographicOperations.FixedTimeEquals(targetHash, expectedHash))
        {
            return false;
        }

        // The source handle denies writers but allows deletion. Deleting while that handle
        // remains open closes the verify/delete race without touching an unverified file.
        File.Delete(sourcePath);
        return !File.Exists(sourcePath);
    }

    private static void TryDeleteEmptyParents(string? directory, string sourceRoot)
    {
        while (!string.IsNullOrWhiteSpace(directory) && IsChildOf(directory, sourceRoot))
        {
            try
            {
                if (!Directory.Exists(directory) || Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    return;
                }
                Directory.Delete(directory, false);
                directory = Path.GetDirectoryName(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    private static async Task<string> WriteReportAsync(
        string targetRoot,
        RecordingRootMigrationReport report,
        CancellationToken cancellationToken)
    {
        var reportsDirectory = Path.Combine(targetRoot, ".unpackvision", "migration-reports");
        Directory.CreateDirectory(reportsDirectory);
        var path = Path.Combine(
            reportsDirectory,
            $"migration-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        var temporaryPath = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, report, cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, path, false);
            return path;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static async Task<string> WriteCleanupReportAsync(
        string targetRoot,
        RecordingRootMigrationCleanupReport report,
        CancellationToken cancellationToken)
    {
        var reportsDirectory = Path.Combine(targetRoot, ".unpackvision", "migration-reports");
        Directory.CreateDirectory(reportsDirectory);
        var path = Path.Combine(
            reportsDirectory,
            $"migration-cleanup-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        var temporaryPath = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, report, cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, path, false);
            return path;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static string NormalizeRoot(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("录像目录不能为空", parameterName);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
    }

    private static void EnsureSeparateRoots(string source, string target)
    {
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase) ||
            IsChildOf(source, target) || IsChildOf(target, source))
        {
            throw new ArgumentException("新旧录像目录不能相同，也不能互为父子目录。");
        }
    }

    private static bool IsChildOf(string candidate, string root) =>
        candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string ResolveChild(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Split(Path.DirectorySeparatorChar).Any(part => part == ".."))
        {
            throw new InvalidDataException("迁移清单包含越界路径。");
        }
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!IsChildOf(fullPath, root)) throw new InvalidDataException("迁移文件超出录像目录。");
        return fullPath;
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? $"{bytes / 1024d / 1024d / 1024d:F2} GB"
            : $"{bytes / 1024d / 1024d:F1} MB";

    private sealed record RecordingRootMigrationReport(
        DateTimeOffset CompletedAt,
        string SourceRoot,
        string TargetRoot,
        int TotalFiles,
        int CopiedFiles,
        int VerifiedExistingFiles,
        long CopiedBytes,
        IReadOnlyList<RecordingRootMigrationFileReceipt> VerifiedFiles);

    private sealed record RecordingRootMigrationCleanupReport(
        DateTimeOffset CompletedAt,
        string MigrationReportPath,
        string SourceRoot,
        string TargetRoot,
        int DeletedFiles,
        long DeletedBytes,
        IReadOnlyList<RecordingRootMigrationCleanupFailure> RemainingFiles);
}
