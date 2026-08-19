using System.Net;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using UnpackVision.Core;
using UnpackVision.Core.Recording;
using UnpackVision.Infrastructure;

namespace UnpackVision.Tests;

public sealed class WorkspaceAndTelemetryTests
{
    [Fact]
    public async Task WorkbookTemplate_CreatesValidatedSixColumnWorkbook()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "generated.xlsx");
            var service = new WorkbookTemplateService();
            await service.CreateAsync(path);
            var validation = await service.ValidateAsync(path);

            Assert.True(validation.Valid, validation.Message);
            using var document = SpreadsheetDocument.Open(path, false);
            var sheets = document.WorkbookPart!.Workbook!.Sheets!.Elements<Sheet>().ToArray();
            Assert.Equal(SheetStateValues.VeryHidden,
                sheets.Single(item => item.Name == WorkbookTemplateService.SyncSheetName).State?.Value);
            Assert.Equal("退货扫码单号", sheets[0].Name?.Value);
        }
        finally
        {
            await DeleteDirectoryWithRetryAsync(root);
        }
    }

    [Fact]
    public async Task WorkbookTemplate_RejectsAWorkbookLockedForEditing()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "locked.xlsx");
            var service = new WorkbookTemplateService();
            await service.CreateAsync(path);
            await using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            var validation = await service.ValidateAsync(path);

            Assert.False(validation.Valid);
            Assert.Contains("占用", validation.Message, StringComparison.Ordinal);
        }
        finally
        {
            await DeleteDirectoryWithRetryAsync(root);
        }
    }

    [Fact]
    public async Task GeneratedWorkbook_DoesNotCreateBrokenLegacyFormula()
    {
        var root = CreateTempDirectory();
        try
        {
            var workbook = Path.Combine(root, "generated.xlsx");
            var video = Path.Combine(root, "record.mp4");
            await File.WriteAllBytesAsync(video, [1, 2, 3]);
            await new WorkbookTemplateService().CreateAsync(workbook);
            var record = new ScanRecord
            {
                TrackingNo = "690123456789",
                State = RecordingState.Completed,
                ScannedAt = DateTimeOffset.Now,
                RecordingStartedAt = DateTimeOffset.Now.AddMinutes(-1),
                RecordingEndedAt = DateTimeOffset.Now,
                VideoPath = video,
                CreatedAt = DateTimeOffset.Now,
                UpdatedAt = DateTimeOffset.Now
            };

            await new ExcelConnector(new ExcelConnectorOptions { WorkbookPath = workbook })
                .PushRecordAsync(record);

            using var document = SpreadsheetDocument.Open(workbook, false);
            var sheet = document.WorkbookPart!.Workbook!.Sheets!.Elements<Sheet>()
                .Single(item => item.Name == "退货扫码单号");
            var part = (WorksheetPart)document.WorkbookPart.GetPartById(sheet.Id!);
            var row = part.Worksheet!.GetFirstChild<SheetData>()!.Elements<Row>()
                .Single(item => item.RowIndex?.Value == 2U);
            var cellC = row.Elements<Cell>().Single(item => item.CellReference == "C2");
            Assert.Null(cellC.CellFormula);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PortableCatalog_UsesRelativePathsAndOmitsSecrets()
    {
        var root = CreateTempDirectory();
        try
        {
            var video = Path.Combine(root, "Unpacking", "record.mp4");
            var segmentVideo = Path.Combine(root, "Unpacking", "record-000.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(video)!);
            await File.WriteAllBytesAsync(video, [1]);
            await File.WriteAllBytesAsync(segmentVideo, [2]);
            var asset = new RecordMediaAsset
            {
                CameraId = "front",
                DisplayName = "主机位",
                Role = RecordMediaRole.Primary,
                VideoPath = video,
                StorageTargetId = "drive-a",
                RelativeVideoPath = "Unpacking/record.mp4",
                Codec = "H264",
                EncoderName = "mfh264enc",
                HardwareAccelerated = true,
                Duration = TimeSpan.FromSeconds(25),
                CreatedAt = DateTimeOffset.Now,
                UpdatedAt = DateTimeOffset.Now
            };
            asset.Segments =
            [
                new RecordMediaSegment
                {
                    MediaAssetId = asset.Id,
                    Sequence = 0,
                    StorageTargetId = asset.StorageTargetId,
                    VideoPath = segmentVideo,
                    Duration = TimeSpan.FromSeconds(25),
                    Recovered = true,
                    CreatedAt = DateTimeOffset.Now
                }
            ];
            var record = new ScanRecord
            {
                TrackingNo = "YT1234567890",
                State = RecordingState.Completed,
                ScannedAt = DateTimeOffset.Now,
                VideoPath = video,
                MediaAssets = [asset],
                DefaultMediaAssetId = asset.Id,
                Note = "破损",
                CreatedAt = DateTimeOffset.Now,
                UpdatedAt = DateTimeOffset.Now
            };
            var catalog = new PortableRecordCatalog(root);
            await catalog.WriteAsync(record);

            var jsonPath = Path.Combine(root, ".unpackvision", "records", $"{record.Id:D}.json");
            var json = await File.ReadAllTextAsync(jsonPath);
            Assert.DoesNotContain(root, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Unpacking/record.mp4", json, StringComparison.Ordinal);
            var portable = Assert.IsType<PortableScanRecord>(Assert.Single(await catalog.ReadAllAsync()).Record);
            var portableAsset = Assert.Single(portable.MediaAssets);
            Assert.Equal("drive-a", portableAsset.StorageTargetId);
            Assert.Equal("H264", portableAsset.Codec);
            Assert.Equal("mfh264enc", portableAsset.EncoderName);
            Assert.True(portableAsset.HardwareAccelerated);
            Assert.Equal(25_000, portableAsset.DurationMilliseconds);
            var portableSegment = Assert.Single(portableAsset.Segments);
            Assert.Equal("Unpacking/record-000.mp4", portableSegment.RelativeVideoPath);
            Assert.True(portableSegment.Recovered);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PortableCatalog_IdempotentlyUpgradesWorkspaceManifestSchema()
    {
        var root = CreateTempDirectory();
        try
        {
            var index = Path.Combine(root, ".unpackvision");
            Directory.CreateDirectory(index);
            var workspaceId = Guid.NewGuid();
            await File.WriteAllTextAsync(
                Path.Combine(index, "workspace.json"),
                $$"""
                  {
                    "schemaVersion": 1,
                    "workspaceId": "{{workspaceId:D}}",
                    "createdAt": "2026-01-01T00:00:00+08:00",
                    "updatedAt": "2026-01-01T00:00:00+08:00"
                  }
                  """);
            var catalog = new PortableRecordCatalog(root);

            var first = await catalog.EnsureWorkspaceAsync();
            var second = await catalog.EnsureWorkspaceAsync();

            Assert.Equal(workspaceId, first.WorkspaceId);
            Assert.Equal(WorkspaceManifest.CurrentSchemaVersion, first.SchemaVersion);
            Assert.Equal(first.SchemaVersion, second.SchemaVersion);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PortableCatalog_TracksExcelDeliveryStatusChanges()
    {
        var root = CreateTempDirectory();
        try
        {
            var options = new StorageOptions
            {
                DatabasePath = Path.Combine(root, "local.db"),
                RecordingRoot = Path.Combine(root, "videos")
            };
            var inner = new SqliteScanRecordRepository(options);
            var catalog = new PortableRecordCatalog(options.RecordingRoot);
            var repository = new PortableCatalogScanRecordRepository(inner, () => catalog);
            await repository.InitializeAsync();
            var record = new ScanRecord
            {
                TrackingNo = "YT1234567890",
                State = RecordingState.Completed,
                ScannedAt = DateTimeOffset.Now,
                CreatedAt = DateTimeOffset.Now,
                UpdatedAt = DateTimeOffset.Now
            };
            await repository.AddAsync(record);
            await repository.EnqueueDeliveryAsync(record.Id, "excel");
            var delivery = Assert.Single(await repository.GetDueDeliveriesAsync(
                10,
                DateTimeOffset.MaxValue));
            Assert.True(await repository.TryClaimDeliveryAsync(delivery.Id));
            await repository.CompleteDeliveryAsync(delivery.Id, "row:2");

            var portable = Assert.Single(await catalog.ReadAllAsync()).Record;
            Assert.NotNull(portable);
            Assert.Equal(SyncStatus.Succeeded.ToString(), portable.ExcelSyncStatus);
        }
        finally
        {
            await DeleteDirectoryWithRetryAsync(root);
        }
    }

    [Fact]
    public async Task PortableCatalogRepository_WritesEachOrderIndexBesideItsStorageTarget()
    {
        var root = CreateTempDirectory();
        try
        {
            var driveA = Path.Combine(root, "drive-a");
            var driveB = Path.Combine(root, "drive-b");
            Directory.CreateDirectory(driveA);
            Directory.CreateDirectory(driveB);
            var options = new StorageOptions
            {
                DatabasePath = Path.Combine(root, "pool.db"),
                RecordingRoot = driveA,
                StoragePool = new StoragePoolOptions
                {
                    Targets =
                    [
                        new StorageTarget { Id = "a", RootPath = driveA, Priority = 0 },
                        new StorageTarget { Id = "b", RootPath = driveB, Priority = 1 }
                    ]
                }
            };
            var inner = new SqliteScanRecordRepository(options);
            var repository = new PortableCatalogScanRecordRepository(inner, options);
            await repository.InitializeAsync();
            var now = DateTimeOffset.Now;
            var pending = new ScanRecord
            {
                TrackingNo = "POOL-B-001",
                State = RecordingState.Recording,
                ScannedAt = now,
                CreatedAt = now,
                UpdatedAt = now
            };
            await repository.AddAsync(pending);
            var videoB = Path.Combine(driveB, "Unpacking", "pool-b.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(videoB)!);
            await File.WriteAllBytesAsync(videoB, [1]);
            pending.State = RecordingState.Completed;
            pending.VideoPath = videoB;
            pending.MediaAssets =
            [
                new RecordMediaAsset
                {
                    RecordId = pending.Id,
                    CameraId = "front",
                    DisplayName = "主机位",
                    Role = RecordMediaRole.Primary,
                    VideoPath = videoB,
                    StorageTargetId = "b",
                    RelativeVideoPath = "Unpacking/pool-b.mp4",
                    CreatedAt = now,
                    UpdatedAt = now
                }
            ];
            await repository.CompleteAndEnqueueAsync(pending, "excel");

            var videoA = Path.Combine(driveA, "Unpacking", "pool-a.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(videoA)!);
            await File.WriteAllBytesAsync(videoA, [2]);
            var onDriveA = new ScanRecord
            {
                TrackingNo = "POOL-A-001",
                State = RecordingState.Imported,
                ScannedAt = now.AddSeconds(1),
                VideoPath = videoA,
                CreatedAt = now,
                UpdatedAt = now
            };
            await repository.AddImportedAsync(onDriveA, null);

            var indexA = Path.Combine(driveA, ".unpackvision", "records");
            var indexB = Path.Combine(driveB, ".unpackvision", "records");
            Assert.True(File.Exists(Path.Combine(indexA, $"{onDriveA.Id:D}.json")));
            Assert.False(File.Exists(Path.Combine(indexA, $"{pending.Id:D}.json")));
            Assert.True(File.Exists(Path.Combine(indexB, $"{pending.Id:D}.json")));
            Assert.False(File.Exists(Path.Combine(indexB, $"{onDriveA.Id:D}.json")));
        }
        finally
        {
            await DeleteDirectoryWithRetryAsync(root);
        }
    }

    [Fact]
    public async Task WorkspaceRecovery_ImportsLegacyRecordingIdempotently()
    {
        var root = CreateTempDirectory();
        try
        {
            var recordingRoot = Path.Combine(root, "videos");
            var unpacking = Path.Combine(recordingRoot, "Unpacking");
            Directory.CreateDirectory(unpacking);
            var video = Path.Combine(
                unpacking,
                "690123456789_20260720103512_20260720104023_异常-破损.mp4");
            await File.WriteAllBytesAsync(video, [1, 2, 3]);
            var options = new StorageOptions
            {
                RecordingRoot = recordingRoot,
                DatabasePath = Path.Combine(root, "local.db")
            };
            var repository = new SqliteScanRecordRepository(options);
            await repository.InitializeAsync();
            var service = new WorkspaceRecoveryService(repository, options);
            var preview = await service.PreviewAsync(recordingRoot, null);
            Assert.Equal(1, preview.FileNameOnlyCount);

            var first = await service.RecoverAsync(preview);
            var second = await service.RecoverAsync(preview);
            var restored = Assert.Single(await repository.QueryAsync());
            Assert.Equal("690123456789", restored.TrackingNo);
            Assert.Equal("破损", Assert.Single(restored.Tags).TagName);
            Assert.Equal(1, first.Added);
            Assert.Equal(0, second.Added);
        }
        finally
        {
            await DeleteDirectoryWithRetryAsync(root);
        }
    }

    [Fact]
    public async Task RecordingRootMigration_DeletesOnlyAfterVerifiedCleanupPhase()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "old-recordings");
            var target = Path.Combine(root, "new-recordings");
            var unpackingVideo = Path.Combine(source, "Unpacking", "first.mp4");
            var packingVideo = Path.Combine(source, "Packing", "second.avi");
            var snapshot = Path.Combine(source, "Snapshots", "20260808", "damage.jpg");
            var incomplete = Path.Combine(source, "Unpacking", "active.partial.mp4");
            await WriteFileAsync(unpackingVideo, [1, 2, 3, 4]);
            await WriteFileAsync(packingVideo, [5, 6, 7]);
            await WriteFileAsync(snapshot, [10, 11, 12]);
            await WriteFileAsync(incomplete, [8, 9]);
            await WriteFileAsync(Path.Combine(source, "merchant-note.txt"), "leave this out");
            var portableCatalog = new PortableRecordCatalog(source);
            await portableCatalog.WriteAsync(new ScanRecord
            {
                TrackingNo = "PORTABLE-TEST-001",
                State = RecordingState.Completed,
                ScannedAt = DateTimeOffset.Now,
                VideoPath = unpackingVideo,
                Snapshots = [snapshot],
                CreatedAt = DateTimeOffset.Now,
                UpdatedAt = DateTimeOffset.Now
            });
            var portableManifest = Path.Combine(source, ".unpackvision", "workspace.json");

            var service = new RecordingRootMigrationService();
            var preview = await service.PreviewAsync(source, target);
            var progressReports = new List<RecordingRootMigrationProgress>();
            var result = await service.MigrateAsync(preview, new CaptureProgress<RecordingRootMigrationProgress>(progressReports));

            Assert.Equal(5, preview.FileCount);
            Assert.Equal(5, result.CopiedFiles);
            Assert.True(File.Exists(unpackingVideo));
            Assert.True(File.Exists(Path.Combine(target, "Unpacking", "first.mp4")));
            Assert.True(File.Exists(Path.Combine(target, "Packing", "second.avi")));
            Assert.True(File.Exists(Path.Combine(target, ".unpackvision", "workspace.json")));
            Assert.True(File.Exists(Path.Combine(target, "Snapshots", "20260808", "damage.jpg")));
            Assert.False(File.Exists(Path.Combine(target, "Unpacking", "active.partial.mp4")));
            Assert.False(File.Exists(Path.Combine(target, "merchant-note.txt")));
            Assert.True(File.Exists(result.ReportPath));
            await service.VerifyAsync(result);
            var finalProgress = progressReports[^1];
            Assert.Equal(preview.FileCount, finalProgress.CompletedFiles);
            Assert.Equal(preview.TotalBytes, finalProgress.CompletedBytes);
            Assert.Equal(preview.TotalBytes, finalProgress.TotalBytes);

            var cleanup = await service.CleanupSourceAsync(result);
            Assert.True(cleanup.IsComplete);
            Assert.Equal(preview.FileCount, cleanup.DeletedFiles);
            Assert.False(File.Exists(unpackingVideo));
            Assert.False(File.Exists(packingVideo));
            Assert.False(File.Exists(portableManifest));
            Assert.False(File.Exists(snapshot));
            Assert.True(File.Exists(incomplete));
            Assert.True(File.Exists(Path.Combine(source, "merchant-note.txt")));
            Assert.True(File.Exists(cleanup.ReportPath));
            var recovered = Assert.Single(await new PortableRecordCatalog(target).ReadAllAsync());
            var relativeVideoPath = Assert.IsType<string>(recovered.Record!.RelativeVideoPath);
            Assert.Equal("Unpacking/first.mp4", relativeVideoPath.Replace('\\', '/'));
            Assert.False(Path.IsPathFullyQualified(relativeVideoPath));
        }
        finally
        {
            await DeleteDirectoryWithRetryAsync(root);
        }
    }

    [Fact]
    public async Task RecordingRootMigration_CleanupPreservesSourceWhenTargetChangedOrMissing()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "old-recordings");
            var target = Path.Combine(root, "new-recordings");
            var changedSource = Path.Combine(source, "Unpacking", "changed.mp4");
            var missingSource = Path.Combine(source, "Unpacking", "missing.mp4");
            await WriteFileAsync(changedSource, [1, 2, 3]);
            await WriteFileAsync(missingSource, [4, 5, 6]);
            var service = new RecordingRootMigrationService();
            var migration = await service.MigrateAsync(await service.PreviewAsync(source, target));

            await File.WriteAllBytesAsync(Path.Combine(target, "Unpacking", "changed.mp4"), [9, 9, 9]);
            File.Delete(Path.Combine(target, "Unpacking", "missing.mp4"));
            await Assert.ThrowsAsync<IOException>(() => service.VerifyAsync(migration));
            var cleanup = await service.CleanupSourceAsync(migration);

            Assert.False(cleanup.IsComplete);
            Assert.Equal(0, cleanup.DeletedFiles);
            Assert.Equal(2, cleanup.RemainingFiles.Count);
            Assert.True(File.Exists(changedSource));
            Assert.True(File.Exists(missingSource));
        }
        finally
        {
            await DeleteDirectoryWithRetryAsync(root);
        }
    }

    [Fact]
    public async Task RecordingRootMigration_CleanupIsIdempotent()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "old-recordings");
            var target = Path.Combine(root, "new-recordings");
            await WriteFileAsync(Path.Combine(source, "Unpacking", "one.mp4"), [1, 2, 3]);
            var service = new RecordingRootMigrationService();
            var migration = await service.MigrateAsync(await service.PreviewAsync(source, target));

            var first = await service.CleanupSourceAsync(migration);
            var second = await service.RetryCleanupAsync(migration);

            Assert.True(first.IsComplete);
            Assert.True(second.IsComplete);
            Assert.Equal(1, first.DeletedFiles);
            Assert.Equal(0, second.DeletedFiles);
        }
        finally
        {
            await DeleteDirectoryWithRetryAsync(root);
        }
    }

    [Fact]
    public async Task AnonymousTelemetry_SendsOnlyOncePerBeijingDay()
    {
        var root = CreateTempDirectory();
        try
        {
            var handler = new RecordingHandler();
            var telemetry = new CloudflareUsageTelemetry(
                new HttpClient(handler),
                new TelemetryOptions
                {
                    Enabled = true,
                    Endpoint = "https://telemetry.example/v1/dau",
                    AppVersion = "2.3.0",
                    Platform = "windows",
                    StateDirectory = root
                });
            await telemetry.TrackAsync("app.daily_active");
            await telemetry.TrackAsync("app.daily_active");

            var request = Assert.Single(handler.Bodies);
            using var document = JsonDocument.Parse(request);
            var rootElement = document.RootElement;
            Assert.Equal("windows", rootElement.GetProperty("platform").GetString());
            Assert.Equal("2.3.0", rootElement.GetProperty("appVersion").GetString());
            Assert.Equal(64, rootElement.GetProperty("dailyId").GetString()!.Length);
            Assert.Equal(5, rootElement.EnumerateObject().Count());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "UnpackVisionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task WriteFileAsync(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);
    }

    private sealed class CaptureProgress<T>(ICollection<T> values) : IProgress<T>
    {
        public void Report(T value) => values.Add(value);
    }

    private static async Task WriteFileAsync(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents);
    }

    private static async Task DeleteDirectoryWithRetryAsync(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(path, true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(100);
            }
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
