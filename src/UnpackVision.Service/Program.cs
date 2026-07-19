using UnpackVision.Core;
using UnpackVision.Infrastructure;
using UnpackVision.Service;

var builder = WebApplication.CreateBuilder(args);
var storageOptions = BindOptions<StorageOptions>(builder.Configuration, "Storage");
var excelOptions = BindOptions<ExcelConnectorOptions>(builder.Configuration, "Excel");
var hikOptions = BindOptions<HikCompatibilityOptions>(builder.Configuration, "HikCompatibility");
var securityOptions = BindOptions<SecurityOptions>(builder.Configuration, "Security");

builder.Services.AddSingleton(storageOptions);
builder.Services.AddSingleton(excelOptions);
builder.Services.AddSingleton(hikOptions);
builder.Services.AddSingleton(securityOptions);
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IScanRecordRepository, SqliteScanRecordRepository>();
builder.Services.AddSingleton<ExcelConnector>();
builder.Services.AddSingleton<ISyncConnector>(services => services.GetRequiredService<ExcelConnector>());
builder.Services.AddSingleton<SyncDispatcher>();
builder.Services.AddSingleton<HikRecordingImporter>();
builder.Services.AddSingleton<IImageScanService, OpenCvImageScanService>();
builder.Services.AddSingleton<IBarcodeRecognitionService, ZxingBarcodeRecognitionService>();
builder.Services.AddSingleton<IOfflineOcrService, OfflineOcrNotConfiguredService>();
builder.Services.AddSingleton(services => new ApiKeyStore(services.GetRequiredService<SecurityOptions>().ApiKeyPath));
builder.Services.AddHostedService<HikWatcherService>();
builder.Services.AddHostedService<SyncWorkerService>();
builder.Services.AddOpenApi();

var app = builder.Build();
var repository = app.Services.GetRequiredService<IScanRecordRepository>();
await repository.InitializeAsync();
var apiKeyStore = app.Services.GetRequiredService<ApiKeyStore>();
var apiKey = await apiKeyStore.GetOrCreateAsync();

if (args.Contains("--show-api-key", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine(apiKey);
    return;
}

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/v1/health") ||
        context.Request.Path.StartsWithSegments("/openapi"))
    {
        await next();
        return;
    }

    if (!context.Request.Headers.TryGetValue("X-UnpackVision-Key", out var supplied) ||
        !string.Equals(supplied.ToString(), apiKey, StringComparison.Ordinal))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "缺少或无效的 X-UnpackVision-Key" });
        return;
    }
    await next();
});

app.MapOpenApi();
app.MapGet("/api/v1/health", async (
    ExcelConnector connector,
    IScanRecordRepository records,
    CancellationToken cancellationToken) =>
{
    ConnectorHealth excelHealth = await connector.GetHealthAsync(cancellationToken);
    var databaseHealthy = true;
    string databaseMessage;
    try
    {
        await records.QueryAsync(limit: 1, cancellationToken: cancellationToken);
        databaseMessage = "SQLite 可读写";
    }
    catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
    {
        databaseHealthy = false;
        databaseMessage = ex.Message;
    }

    return Results.Ok(new
    {
        status = databaseHealthy && excelHealth.Healthy ? "healthy" : "degraded",
        database = new { healthy = databaseHealthy, message = databaseMessage },
        excel = excelHealth,
        time = DateTimeOffset.Now
    });
});

app.MapGet("/api/v1/records", async (
    string? trackingNo,
    int? limit,
    IScanRecordRepository records,
    CancellationToken cancellationToken) =>
    Results.Ok(await records.QueryAsync(trackingNo, limit ?? 200, cancellationToken)));

app.MapGet("/api/v1/records/{id:guid}", async (
    Guid id,
    IScanRecordRepository records,
    CancellationToken cancellationToken) =>
{
    var record = await records.GetAsync(id, cancellationToken);
    return record is null ? Results.NotFound() : Results.Ok(record);
});

app.MapGet("/api/v1/records/by-tracking/{trackingNo}", async (
    string trackingNo,
    IScanRecordRepository records,
    CancellationToken cancellationToken) =>
    Results.Ok(await records.QueryAsync(trackingNo, 500, cancellationToken)));

app.MapPost("/api/v1/records", async (
    CreateRecordRequest request,
    IScanRecordRepository records,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.TrackingNo) ||
        string.IsNullOrWhiteSpace(request.VideoPath) ||
        !File.Exists(request.VideoPath))
    {
        return Results.BadRequest(new { error = "trackingNo 和存在的 videoPath 为必填项" });
    }

    var now = DateTimeOffset.Now;
    var record = new ScanRecord
    {
        TrackingNo = request.TrackingNo.Trim(),
        Workflow = request.Workflow,
        State = RecordingState.Imported,
        ScannedAt = request.ScannedAt ?? now,
        RecordingStartedAt = request.RecordingStartedAt ?? request.ScannedAt ?? now,
        RecordingEndedAt = request.RecordingEndedAt ?? now,
        VideoPath = Path.GetFullPath(request.VideoPath),
        CreatedAt = now,
        UpdatedAt = now
    };
    await records.AddImportedAsync(record, "excel", cancellationToken);
    return Results.Created($"/api/v1/records/{record.Id}", record);
});

app.MapPost("/api/v1/connectors/{id}/retry", async (
    string id,
    IScanRecordRepository records,
    CancellationToken cancellationToken) =>
{
    await records.RetryConnectorAsync(id, cancellationToken);
    return Results.Accepted();
});

app.MapPost("/api/v1/images/process", async (
    ImageProcessRequest request,
    IImageScanService imageService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await imageService.ProcessAsync(request, cancellationToken));
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/v1/images/barcodes", async (
    BarcodeRequest request,
    IBarcodeRecognitionService barcodeService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await barcodeService.RecognizeAsync(request.ImagePath, cancellationToken));
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/v1/ocr/health", (IOfflineOcrService ocr) => Results.Ok(new
{
    available = ocr.IsAvailable,
    message = ocr.AvailabilityMessage,
    cloudFallback = false
}));

await app.RunAsync();

static T BindOptions<T>(IConfiguration configuration, string sectionName) where T : new()
{
    var value = configuration.GetSection(sectionName).Get<T>() ?? new T();
    foreach (var property in typeof(T).GetProperties().Where(property => property.PropertyType == typeof(string)))
    {
        if (property.GetValue(value) is string text)
        {
            property.SetValue(value, Environment.ExpandEnvironmentVariables(text));
        }
    }
    return value;
}

public sealed record CreateRecordRequest(
    string TrackingNo,
    string VideoPath,
    WorkflowMode Workflow = WorkflowMode.Unpacking,
    DateTimeOffset? ScannedAt = null,
    DateTimeOffset? RecordingStartedAt = null,
    DateTimeOffset? RecordingEndedAt = null);

public sealed record BarcodeRequest(string ImagePath);
