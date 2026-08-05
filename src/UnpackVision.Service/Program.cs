using System.Diagnostics;
using UnpackVision.Core;
using UnpackVision.Infrastructure;
using UnpackVision.Infrastructure.Diagnostics;
using UnpackVision.Service;

DiagnosticLog.Initialize("sync-service", "2.3.2");
DiagnosticLog.RegisterGlobalExceptionHandlers();
DiagnosticLog.Information("兼容同步服务正在启动");

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddProvider(DiagnosticLog.CreateLoggerProvider());
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
var storageOptions = BindOptions<StorageOptions>(builder.Configuration, "Storage");
var excelOptions = BindOptions<ExcelConnectorOptions>(builder.Configuration, "Excel");
var localSettings = await new LocalSettingsStore().LoadAsync();
var allowTestInstance = string.Equals(
    Environment.GetEnvironmentVariable("UNPACKVISION_ALLOW_TEST_INSTANCE"),
    "1",
    StringComparison.Ordinal);
if (!allowTestInstance)
{
    // Production follows the desktop settings. Smoke tests keep their explicit
    // temporary paths so validation can never read or write real business data.
    storageOptions.RecordingRoot = localSettings.RecordingRoot;
    excelOptions.WorkbookPath = localSettings.ExcelWorkbookPath;
}
var hikOptions = BindOptions<HikCompatibilityOptions>(builder.Configuration, "HikCompatibility");
var securityOptions = BindOptions<SecurityOptions>(builder.Configuration, "Security");
var webhookOptions = BindOptions<WebhookOptions>(builder.Configuration, "Webhooks");

builder.Services.AddSingleton(storageOptions);
builder.Services.AddSingleton(excelOptions);
builder.Services.AddSingleton(hikOptions);
builder.Services.AddSingleton(securityOptions);
builder.Services.AddSingleton(webhookOptions);
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<SqliteScanRecordRepository>();
builder.Services.AddSingleton<IScanRecordRepository>(services =>
    new PortableCatalogScanRecordRepository(
        services.GetRequiredService<SqliteScanRecordRepository>(),
        () => new PortableRecordCatalog(storageOptions.RecordingRoot)));
builder.Services.AddSingleton<ExcelConnector>();
builder.Services.AddSingleton<ISyncConnector>(services => services.GetRequiredService<ExcelConnector>());
builder.Services.AddSingleton<SyncDispatcher>();
builder.Services.AddSingleton<HikRecordingImporter>();
builder.Services.AddSingleton<IImageScanService, OpenCvImageScanService>();
builder.Services.AddSingleton<IBarcodeRecognitionService, ZxingBarcodeRecognitionService>();
builder.Services.AddSingleton<IOfflineOcrService, OfflineOcrNotConfiguredService>();
builder.Services.AddHttpClient<WebhookEventPublisher>(client => client.Timeout = TimeSpan.FromSeconds(Math.Clamp(webhookOptions.TimeoutSeconds, 2, 60)));
builder.Services.AddSingleton<IEventPublisher>(services => webhookOptions.Endpoints.Count == 0
    ? new NullEventPublisher()
    : services.GetRequiredService<WebhookEventPublisher>());
builder.Services.AddSingleton<LocalSettingsStore>();
builder.Services.AddSingleton(services => new ApiKeyStore(services.GetRequiredService<SecurityOptions>().ApiKeyPath));
builder.Services.AddHostedService<HikWatcherService>();
builder.Services.AddHostedService<SyncWorkerService>();
builder.Services.AddOpenApi();

var app = builder.Build();
var requestLogger = app.Services
    .GetRequiredService<ILoggerFactory>()
    .CreateLogger("UnpackVision.Requests");
var repository = app.Services.GetRequiredService<IScanRecordRepository>();
await repository.InitializeAsync();
var apiKeyStore = app.Services.GetRequiredService<ApiKeyStore>();
var apiKey = await apiKeyStore.GetOrCreateAsync();

if (args.Contains("--show-api-key", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine(apiKey);
    DiagnosticLog.Information("按显式命令显示本机 API 密钥后退出");
    DiagnosticLog.CloseAndFlush();
    return;
}

app.Use(async (context, next) =>
{
    var startedAt = Stopwatch.GetTimestamp();
    try
    {
        await next();
    }
    catch (Exception exception)
    {
        requestLogger.LogError(
            exception,
            "HTTP 请求失败，端点 {EndpointTemplate}，方法 {HttpMethod}",
            GetEndpointTemplate(context),
            context.Request.Method);
        throw;
    }
    finally
    {
        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        if (elapsed >= TimeSpan.FromSeconds(2) ||
            context.Response.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            requestLogger.LogWarning(
                "HTTP 请求缓慢或失败，端点 {EndpointTemplate}，方法 {HttpMethod}，状态 {StatusCode}，耗时 {ElapsedMilliseconds} 毫秒",
                GetEndpointTemplate(context),
                context.Request.Method,
                context.Response.StatusCode,
                elapsed.TotalMilliseconds);
        }
    }
});

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

app.MapGet("/api/v1/tags", async (LocalSettingsStore settingsStore, CancellationToken cancellationToken) =>
{
    var settings = await settingsStore.LoadAsync(cancellationToken);
    return Results.Ok(settings.IssueTags.Where(tag => tag.Enabled).OrderBy(tag => tag.SortOrder));
});

app.MapPost("/api/v1/records/{id:guid}/tags/{tagId}", async Task<IResult> (
    Guid id,
    string tagId,
    IScanRecordRepository records,
    LocalSettingsStore settingsStore,
    IEventPublisher events,
    CancellationToken cancellationToken) =>
{
    var record = await records.GetAsync(id, cancellationToken);
    if (record is null) return Results.NotFound();
    var settings = await settingsStore.LoadAsync(cancellationToken);
    var definition = settings.IssueTags.FirstOrDefault(tag => tag.Enabled && string.Equals(tag.Id, tagId, StringComparison.OrdinalIgnoreCase));
    if (definition is null) return Results.NotFound(new { error = "异常标签不存在或已停用" });
    await records.AddTagAsync(id, definition, DateTimeOffset.Now, "api", cancellationToken);
    record = await records.GetAsync(id, cancellationToken) ?? record;
    if (record.State is RecordingState.Completed or RecordingState.Imported) await records.EnqueueDeliveryAsync(id, "excel", cancellationToken);
    await events.PublishAsync("record.tagged", record, cancellationToken);
    return Results.Ok(record);
});

app.MapDelete("/api/v1/records/{id:guid}/tags/{assignmentId:guid}", async Task<IResult> (
    Guid id,
    Guid assignmentId,
    IScanRecordRepository records,
    IEventPublisher events,
    CancellationToken cancellationToken) =>
{
    var removed = await records.RemoveTagAsync(id, assignmentId, DateTimeOffset.Now, cancellationToken);
    if (removed is null) return Results.NotFound();
    var record = await records.GetAsync(id, cancellationToken);
    if (record is null) return Results.NotFound();
    if (record.State is RecordingState.Completed or RecordingState.Imported) await records.EnqueueDeliveryAsync(id, "excel", cancellationToken);
    await events.PublishAsync("record.tag_removed", record, cancellationToken);
    return Results.Ok(record);
});

app.MapPut("/api/v1/records/{id:guid}/note", async Task<IResult> (
    Guid id,
    UpdateNoteRequest request,
    IScanRecordRepository records,
    IEventPublisher events,
    CancellationToken cancellationToken) =>
{
    if (request.Note?.Length > 2000) return Results.BadRequest(new { error = "备注不能超过 2000 个字符" });
    if (await records.GetAsync(id, cancellationToken) is null) return Results.NotFound();
    await records.UpdateNoteAsync(id, request.Note ?? string.Empty, DateTimeOffset.Now, cancellationToken);
    var record = await records.GetAsync(id, cancellationToken);
    if (record is null) return Results.NotFound();
    if (record.State is RecordingState.Completed or RecordingState.Imported) await records.EnqueueDeliveryAsync(id, "excel", cancellationToken);
    await events.PublishAsync("record.note_updated", record, cancellationToken);
    return Results.Ok(record);
});

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

try
{
    await app.RunAsync();
}
finally
{
    DiagnosticLog.Information("兼容同步服务正在停止");
    DiagnosticLog.CloseAndFlush();
}

static string GetEndpointTemplate(HttpContext context) =>
    context.GetEndpoint() is RouteEndpoint routeEndpoint
        ? routeEndpoint.RoutePattern.RawText ?? "unknown"
        : "unmatched";

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
public sealed record UpdateNoteRequest(string? Note);
