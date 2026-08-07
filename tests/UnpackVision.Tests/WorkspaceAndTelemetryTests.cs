using System.Net;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using UnpackVision.Core;
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
            Directory.CreateDirectory(Path.GetDirectoryName(video)!);
            await File.WriteAllBytesAsync(video, [1]);
            var record = new ScanRecord
            {
                TrackingNo = "YT1234567890",
                State = RecordingState.Completed,
                ScannedAt = DateTimeOffset.Now,
                VideoPath = video,
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
            Assert.Single(await catalog.ReadAllAsync());
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
