using System.Text.Json;
using UnpackVision.Core;
using UnpackVision.Infrastructure;

namespace UnpackVision.Tests;

public sealed class LegacyRecordingMigrationRepairTests
{
    [Fact]
    public async Task LegacyCopyOnlyMigration_IsReverifiedRebasedAndCleanedWithoutUnrelatedFiles()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "old-recordings");
            var target = Path.Combine(root, "new-recordings");
            var sourceVideo = Path.Combine(source, "Unpacking", "parcel.mp4");
            var targetVideo = Path.Combine(target, "Unpacking", "parcel.mp4");
            var partial = Path.Combine(source, "Unpacking", "active.partial.mp4");
            var unrelated = Path.Combine(source, "merchant-note.txt");
            await WriteFileAsync(sourceVideo, [1, 2, 3, 4]);
            await WriteFileAsync(targetVideo, [1, 2, 3, 4]);
            await WriteFileAsync(partial, [9, 9]);
            await WriteFileAsync(unrelated, [8, 8]);

            var workspaceId = Guid.NewGuid();
            await new PortableRecordCatalog(source).EnsureWorkspaceAsync(workspaceId);
            Directory.CreateDirectory(Path.Combine(target, ".unpackvision"));
            File.Copy(
                Path.Combine(source, ".unpackvision", "workspace.json"),
                Path.Combine(target, ".unpackvision", "workspace.json"));
            await WriteLegacyReportAsync(source, target, totalFiles: 2);

            var repository = new InMemoryRepository();
            repository.Records.Add(new ScanRecord
            {
                TrackingNo = "SAFE-LEGACY-001",
                State = RecordingState.Completed,
                ScannedAt = DateTimeOffset.Now,
                VideoPath = sourceVideo,
                CreatedAt = DateTimeOffset.Now,
                UpdatedAt = DateTimeOffset.Now
            });
            var options = new StorageOptions
            {
                RecordingRoot = target,
                DatabasePath = Path.Combine(root, "database-does-not-exist.db")
            };
            var service = new LegacyRecordingMigrationRepairService(repository, options);

            var candidate = Assert.IsType<LegacyRecordingMigrationRepairCandidate>(
                await service.FindPendingAsync(target));
            Assert.Equal(1, candidate.PersistedSourcePathCount);

            var result = await service.RepairAsync(candidate);

            Assert.Equal(1, result.RebasedPaths);
            Assert.True(result.Cleanup.IsComplete);
            Assert.False(File.Exists(sourceVideo));
            Assert.True(File.Exists(targetVideo));
            Assert.True(File.Exists(partial));
            Assert.True(File.Exists(unrelated));
            Assert.Equal(targetVideo, repository.Records.Single().VideoPath);
            Assert.True(File.Exists(result.Cleanup.ReportPath));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Discovery_RejectsReportWhenWorkspaceIdentityDoesNotMatch()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "old-recordings");
            var target = Path.Combine(root, "new-recordings");
            await WriteFileAsync(Path.Combine(source, "Unpacking", "parcel.mp4"), [1]);
            await WriteFileAsync(Path.Combine(target, "Unpacking", "parcel.mp4"), [1]);
            await new PortableRecordCatalog(source).EnsureWorkspaceAsync(Guid.NewGuid());
            await new PortableRecordCatalog(target).EnsureWorkspaceAsync(Guid.NewGuid());
            await WriteLegacyReportAsync(source, target, totalFiles: 2);

            var service = new LegacyRecordingMigrationRepairService(
                new InMemoryRepository(),
                new StorageOptions { RecordingRoot = target, DatabasePath = Path.Combine(root, "none.db") });

            Assert.Null(await service.FindPendingAsync(target));
            Assert.True(File.Exists(Path.Combine(source, "Unpacking", "parcel.mp4")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Discovery_DoesNotDowngradeReceiptBasedMigrationReportToLegacyCleanup()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "old-recordings");
            var target = Path.Combine(root, "new-recordings");
            await WriteFileAsync(Path.Combine(source, "Unpacking", "parcel.mp4"), [1]);
            await WriteFileAsync(Path.Combine(target, "Unpacking", "parcel.mp4"), [1]);
            var workspaceId = Guid.NewGuid();
            await new PortableRecordCatalog(source).EnsureWorkspaceAsync(workspaceId);
            await new PortableRecordCatalog(target).EnsureWorkspaceAsync(workspaceId);
            var reports = Path.Combine(target, ".unpackvision", "migration-reports");
            Directory.CreateDirectory(reports);
            await File.WriteAllTextAsync(
                Path.Combine(reports, "migration-20260813-140000-0123456789abcdef0123456789abcdef.json"),
                JsonSerializer.Serialize(new
                {
                    CompletedAt = DateTimeOffset.Now,
                    SourceRoot = source,
                    TargetRoot = target,
                    TotalFiles = 2,
                    CopiedFiles = 2,
                    VerifiedExistingFiles = 0,
                    CopiedBytes = 10,
                    VerifiedFiles = Array.Empty<object>()
                }));
            var service = new LegacyRecordingMigrationRepairService(
                new InMemoryRepository(),
                new StorageOptions { RecordingRoot = target, DatabasePath = Path.Combine(root, "none.db") });

            Assert.Null(await service.FindPendingAsync(target));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task WriteLegacyReportAsync(string source, string target, int totalFiles)
    {
        var reports = Path.Combine(target, ".unpackvision", "migration-reports");
        Directory.CreateDirectory(reports);
        await File.WriteAllTextAsync(
            Path.Combine(reports, "migration-20260813-135500.json"),
            JsonSerializer.Serialize(new
            {
                CompletedAt = DateTimeOffset.Now,
                SourceRoot = source,
                TargetRoot = target,
                TotalFiles = totalFiles,
                CopiedFiles = totalFiles,
                VerifiedExistingFiles = 0,
                CopiedBytes = 10
            }));
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "UnpackVision.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task WriteFileAsync(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);
    }
}
