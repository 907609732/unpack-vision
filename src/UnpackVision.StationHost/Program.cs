using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;
using UnpackVision.Application.Recording;
using UnpackVision.Application.Scanning;
using UnpackVision.Core;
using UnpackVision.Infrastructure;
using UnpackVision.Infrastructure.Diagnostics;
using UnpackVision.StationHost;
using static UnpackVision.StationHost.StationHostEndpointSupport;

DiagnosticLog.Initialize("station-host", "2.3.2");
DiagnosticLog.RegisterGlobalExceptionHandlers();
DiagnosticLog.Information("工位主机正在启动");

var allowTestInstance = string.Equals(
    Environment.GetEnvironmentVariable("UNPACKVISION_ALLOW_TEST_INSTANCE"),
    "1",
    StringComparison.Ordinal);
var isFirstHostInstance = true;
using var hostInstanceMutex = allowTestInstance
    ? null
    : new Mutex(
        true,
        @"Local\UnpackVision.StationHost.SingleInstance",
        out isFirstHostInstance);
if (!allowTestInstance && !isFirstHostInstance)
{
    DiagnosticLog.Information("已有工位主机实例正在运行，本实例退出");
    DiagnosticLog.CloseAndFlush();
    return;
}

var settingsStore = new LocalSettingsStore();
var localSettings = await settingsStore.LoadAsync();
var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddProvider(DiagnosticLog.CreateLoggerProvider());
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
var storageOptions = BindOptions<StorageOptions>(builder.Configuration, "Storage");
var excelOptions = BindOptions<ExcelConnectorOptions>(builder.Configuration, "Excel");
if (!allowTestInstance)
{
    // The desktop settings are authoritative in production. Isolated smoke
    // hosts must use their explicit temporary paths and never touch user data.
    storageOptions.RecordingRoot = localSettings.RecordingRoot;
    excelOptions.WorkbookPath = localSettings.ExcelWorkbookPath;
}
var stationOptions = BindOptions<StationHostOptions>(builder.Configuration, "StationHost");
var mediaRelayOptions = BindOptions<MediaRelayOptions>(builder.Configuration, "MediaRelay");
if (string.IsNullOrWhiteSpace(stationOptions.StationId))
{
    stationOptions.StationId = Environment.MachineName;
}
var lanAddresses = GetPrivateIpv4Addresses();
stationOptions.LanHttpsEnabled =
    stationOptions.LanHttpsEnabled && lanAddresses.Length > 0;
DiagnosticLog.Information(
    "工位主机检测到 {LanAddressCount} 个专用网络地址，将分别绑定 5273 端口",
    lanAddresses.Length);
var certificateMaterial = StationCertificateStore.LoadOrCreate(
    stationOptions.StationId,
    lanAddresses,
    stationOptions.SecurityDirectory);
stationOptions.CertificateFingerprint = certificateMaterial.Fingerprint;
stationOptions.LanHttpPrototypeEnabled = false;
if (string.IsNullOrWhiteSpace(stationOptions.AdvertisedAddress) && lanAddresses.FirstOrDefault() is { } preferredAddress)
{
    stationOptions.AdvertisedAddress =
        $"https://{preferredAddress}:{stationOptions.LanHttpsPort}";
}
mediaRelayOptions.CertificatePath = certificateMaterial.CertificatePemPath;
mediaRelayOptions.PrivateKeyPath = certificateMaterial.PrivateKeyPemPath;
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 4 * 1024 * 1024;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
    options.ListenLocalhost(stationOptions.LoopbackPort);
    if (stationOptions.LanHttpsEnabled)
    {
        foreach (var address in lanAddresses)
        {
            options.Listen(
                address,
                stationOptions.LanHttpsPort,
                listen => listen.UseHttps(certificateMaterial.Certificate));
        }
    }
});

builder.Services.AddSingleton(storageOptions);
builder.Services.AddSingleton(excelOptions);
builder.Services.AddSingleton(stationOptions);
builder.Services.AddSingleton(mediaRelayOptions);
builder.Services.AddSingleton(localSettings);
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<SqliteScanRecordRepository>();
builder.Services.AddSingleton<IScanRecordRepository>(services =>
    new PortableCatalogScanRecordRepository(
        services.GetRequiredService<SqliteScanRecordRepository>(),
        () => new PortableRecordCatalog(storageOptions.RecordingRoot)));
builder.Services.AddSingleton<IPairedDeviceRegistry, SqlitePairedDeviceRegistry>();
builder.Services.AddSingleton<IScanCommandLedger, SqliteScanCommandLedger>();
builder.Services.AddSingleton<IMediaRelayManager, MediaRelayManager>();
builder.Services.AddSingleton<IEventPublisher, NullEventPublisher>();
builder.Services.AddSingleton<IRecordingBackend>(_ =>
    new OpenCvRecordingBackend(storageOptions, localSettings.Camera));
builder.Services.AddSingleton<RecordingCoordinator>(services => new RecordingCoordinator(
    services.GetRequiredService<IScanRecordRepository>(),
    services.GetRequiredService<IRecordingBackend>(),
    services.GetRequiredService<IEventPublisher>(),
    services.GetRequiredService<IClock>(),
    localSettings.Scanner,
    excelOptions.ConnectorId));
builder.Services.AddSingleton<IScanCommandRouter>(services => new StationScanCommandRouter(
    services.GetRequiredService<RecordingCoordinator>(),
    services.GetRequiredService<IScanRecordRepository>(),
    services.GetRequiredService<IClock>(),
    localSettings.Scanner,
    stationOptions.StationId,
    excelOptions.ConnectorId,
    localSettings.IssueTags,
    services.GetRequiredService<IScanCommandLedger>()));
builder.Services.AddSingleton<PairingSessionStore>();
builder.Services.AddSingleton<DesktopCommandBridge>();
builder.Services.AddHostedService<StationDiscoveryPublisher>();
builder.Services.AddSignalR();
builder.Services.AddOpenApi();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("pairing", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.AddPolicy("device", context => RateLimitPartition.GetTokenBucketLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 120,
            TokensPerPeriod = 60,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();
var requestLogger = app.Services
    .GetRequiredService<ILoggerFactory>()
    .CreateLogger("UnpackVision.Requests");
var pairingLogger = app.Services
    .GetRequiredService<ILoggerFactory>()
    .CreateLogger("UnpackVision.Pairing");
var repository = app.Services.GetRequiredService<IScanRecordRepository>();
await repository.InitializeAsync();
var deviceRegistry = app.Services.GetRequiredService<IPairedDeviceRegistry>();
await deviceRegistry.InitializeAsync();
var commandLedger = app.Services.GetRequiredService<IScanCommandLedger>();
await commandLedger.InitializeAsync();

const string securityGenerationKey = "station-security-generation";
if (!string.Equals(
        await repository.GetMetadataAsync(securityGenerationKey),
        "2.2.0",
        StringComparison.Ordinal))
{
    foreach (var device in await deviceRegistry.GetAllAsync())
    {
        await deviceRegistry.DeleteAsync(device.Id);
    }
    await repository.SetMetadataAsync(securityGenerationKey, "2.2.0");
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
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    var requestHost = context.Request.Host.Host;
    if (!IsLoopback(context) &&
        !lanAddresses.Any(address => string.Equals(address.ToString(), requestHost, StringComparison.OrdinalIgnoreCase)))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = "Host 不属于当前工位安全地址" });
        return;
    }
    if (context.Request.Path.StartsWithSegments("/openapi") && !IsLoopback(context))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    var maximumBody = context.Request.Path.StartsWithSegments("/device/v1/pair") ||
                      context.Request.Path.Value?.EndsWith("/scans", StringComparison.OrdinalIgnoreCase) == true
        ? 64 * 1024
        : 4 * 1024 * 1024;
    if (context.Request.ContentLength > maximumBody)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        return;
    }
    await next();
});
app.MapOpenApi();
app.MapGet("/api/v1/health", () => Results.Ok(new
{
    status = "healthy",
    version = "2.3.2",
    tls = stationOptions.LanHttpsEnabled,
    // The desktop compares this startup snapshot with current Windows network
    // addresses. A mismatch means Wi-Fi, Ethernet or tethering changed and the
    // host must restart before a pairing QR can safely advertise the new IP.
    lanAddresses = lanAddresses.Select(address => address.ToString()).ToArray(),
    time = DateTimeOffset.Now
}));
app.MapPost("/internal/shutdown", (
    HttpRequest request,
    IHostApplicationLifetime lifetime) =>
{
    if (!IsLoopback(request.HttpContext))
    {
        return Results.Unauthorized();
    }
    lifetime.StopApplication();
    return Results.Accepted();
});

app.MapGet("/api/v1/records", async Task<IResult> (
    string? trackingNo,
    string? cursor,
    int? limit,
    HttpContext context,
    IScanRecordRepository records,
    IPairedDeviceRegistry devices,
    CancellationToken cancellationToken) =>
{
    if (!await AuthorizeAsync(context, devices, "records:read", cancellationToken))
    {
        return Results.Unauthorized();
    }
    if (!TryDecodeCursor(cursor, out var offset))
    {
        return Results.BadRequest(new { error = "cursor 无效" });
    }
    var pageSize = Math.Clamp(limit ?? 100, 1, 200);
    var page = await records.QueryPageAsync(trackingNo, offset, pageSize, cancellationToken);
    var items = page.Select(ToStationRecordView).ToArray();
    var nextCursor = page.Count == pageSize ? EncodeCursor(offset + page.Count) : null;
    return Results.Ok(new CursorPage<StationRecordView>(items, nextCursor));
});

app.MapPost("/api/v1/records", async Task<IResult> (
    StationRecordImportRequest request,
    HttpContext context,
    IScanRecordRepository records,
    CancellationToken cancellationToken) =>
{
    if (!IsLoopback(context))
    {
        return Results.Unauthorized();
    }
    if (string.IsNullOrWhiteSpace(request.TrackingNo) ||
        request.TrackingNo.Length > 128 ||
        string.IsNullOrWhiteSpace(request.VideoPath) ||
        !File.Exists(request.VideoPath))
    {
        return Results.BadRequest(new { error = "trackingNo 和本机存在的 videoPath 为必填项" });
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
        StationId = stationOptions.StationId,
        CreatedAt = now,
        UpdatedAt = now
    };
    await records.AddImportedAsync(record, null, cancellationToken);
    return Results.Created($"/api/v1/records/{record.Id}", ToStationRecordView(record));
});

app.MapGet("/api/v1/records/{id:guid}", async Task<IResult> (
    Guid id,
    HttpContext context,
    IScanRecordRepository records,
    IPairedDeviceRegistry devices,
    CancellationToken cancellationToken) =>
{
    if (!await AuthorizeAsync(context, devices, "records:read", cancellationToken))
    {
        return Results.Unauthorized();
    }
    var record = await records.GetAsync(id, cancellationToken);
    return record is null ? Results.NotFound() : Results.Ok(ToStationRecordView(record));
});

app.MapGet("/api/v1/records/{id:guid}/video", async Task<IResult> (
    Guid id,
    HttpContext context,
    IScanRecordRepository records,
    IPairedDeviceRegistry devices,
    CancellationToken cancellationToken) =>
{
    if (!await AuthorizeAsync(context, devices, "video:read", cancellationToken))
    {
        return Results.Unauthorized();
    }
    var record = await records.GetAsync(id, cancellationToken);
    if (record?.VideoPath is not { Length: > 0 } videoPath ||
        !IsPathUnderRoot(videoPath, storageOptions.RecordingRoot) ||
        !File.Exists(videoPath))
    {
        return Results.NotFound(new { error = "录像文件不存在" });
    }
    var info = new FileInfo(videoPath);
    var etag = new EntityTagHeaderValue($"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"");
    return Results.File(
        videoPath,
        GetVideoContentType(info.Extension),
        lastModified: new DateTimeOffset(info.LastWriteTimeUtc),
        entityTag: etag,
        enableRangeProcessing: true);
});

app.MapGet("/api/v1/records/{id:guid}/thumbnail", async Task<IResult> (
    Guid id,
    HttpContext context,
    IScanRecordRepository records,
    IPairedDeviceRegistry devices,
    CancellationToken cancellationToken) =>
{
    if (!await AuthorizeAsync(context, devices, "video:read", cancellationToken))
    {
        return Results.Unauthorized();
    }
    var record = await records.GetAsync(id, cancellationToken);
    var snapshot = record?.Snapshots.FirstOrDefault(path =>
        IsPathUnderRoot(path, storageOptions.RecordingRoot) && File.Exists(path));
    return snapshot is null
        ? Results.NotFound(new { error = "缩略图不存在" })
        : Results.File(snapshot, GetImageContentType(Path.GetExtension(snapshot)), enableRangeProcessing: true);
});

app.MapGet("/api/v1/records/{id:guid}/events", async Task<IResult> (
    Guid id,
    HttpContext context,
    IScanRecordRepository records,
    IPairedDeviceRegistry devices,
    CancellationToken cancellationToken) =>
{
    if (!await AuthorizeAsync(context, devices, "records:read", cancellationToken))
    {
        return Results.Unauthorized();
    }
    var record = await records.GetAsync(id, cancellationToken);
    if (record is null)
    {
        return Results.NotFound();
    }
    var tags = await records.GetTagsAsync(id, true, cancellationToken);
    var events = BuildRecordEvents(record, tags);
    return Results.Ok(events);
});

app.MapGet("/api/v1/stations/{id}/state", async Task<IResult> (
    string id,
    HttpRequest request,
    IScanCommandRouter router,
    DesktopCommandBridge desktopBridge,
    IPairedDeviceRegistry devices,
    CancellationToken cancellationToken) =>
{
    if (!string.Equals(id, stationOptions.StationId, StringComparison.OrdinalIgnoreCase))
    {
        return Results.NotFound();
    }
    if (!await AuthorizeAsync(request.HttpContext, devices, "records:read", cancellationToken))
    {
        return Results.Unauthorized();
    }
    var desktopState = await desktopBridge.TryGetStateAsync(cancellationToken);
    var state = desktopState ?? router.GetState();
    return Results.Ok(new
    {
        state.StationId,
        state.RecordingState,
        state.RecordId,
        state.TrackingNo,
        state.ServerTime,
        state.MediaRelayRunning,
        desktopReady = desktopState is not null
    });
}).RequireRateLimiting("device");

app.MapPost("/api/v1/stations/{id}/scans", async Task<IResult> (
    string id,
    ScanCommand command,
    HttpRequest request,
    IScanCommandRouter router,
    DesktopCommandBridge desktopBridge,
    IPairedDeviceRegistry devices,
    CancellationToken cancellationToken) =>
{
    if (!string.Equals(id, stationOptions.StationId, StringComparison.OrdinalIgnoreCase))
    {
        return Results.NotFound();
    }
    if (!string.Equals(command.StationId, id, StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "命令中的 stationId 与请求地址不一致" });
    }
    if (command.EventId == Guid.Empty ||
        string.IsNullOrWhiteSpace(command.DeviceId) ||
        command.DeviceId.Length > 128 ||
        string.IsNullOrWhiteSpace(command.Value) ||
        command.Value.Length > 512 ||
        string.IsNullOrWhiteSpace(command.IdempotencyKey) ||
        command.IdempotencyKey.Length > 128)
    {
        return Results.BadRequest(new { error = "扫码命令字段为空或超过安全长度" });
    }
    var isLoopback = IsLoopback(request.HttpContext);
    if (!isLoopback)
    {
        var authorization = ReadDeviceAuthorization(request);
        if (authorization is null)
        {
            return Results.Unauthorized();
        }
        var device = await devices.AuthenticateAsync(
            authorization.Value.DeviceId,
            authorization.Value.AccessToken,
            "scan:send",
            cancellationToken);
        if (device is null || !string.Equals(device.Id, command.DeviceId, StringComparison.Ordinal))
        {
            return Results.Unauthorized();
        }
    }
    if (!isLoopback && command.Mode is DeviceOperatingMode.HandheldScanner or DeviceOperatingMode.IssueRemote)
    {
        var acknowledgement = await desktopBridge.TryRouteAsync(command, cancellationToken);
        return acknowledgement is null
            ? Results.Json(new { error = "电脑桌面端未就绪，请先打开拆包智录" }, statusCode: StatusCodes.Status503ServiceUnavailable)
            : Results.Ok(acknowledgement);
    }

    return Results.Ok(await router.RouteAsync(command, cancellationToken));
}).RequireRateLimiting("device");

app.MapPost("/api/v1/pairing/sessions", (
    string? address,
    HttpRequest request,
    PairingSessionStore sessions) =>
{
    if (!IsLoopback(request.HttpContext))
    {
        return Results.Unauthorized();
    }
    var requestAddress = Uri.TryCreate(stationOptions.AdvertisedAddress, UriKind.Absolute, out var advertised)
        ? advertised
        : new Uri("https://127.0.0.1:5273");
    Uri? selectedAddress = null;
    if (!string.IsNullOrWhiteSpace(address))
    {
        if (!IPAddress.TryParse(address, out var parsed) ||
            !lanAddresses.Contains(parsed))
        {
            pairingLogger.LogWarning("拒绝为不属于当前专用网络的地址创建配对会话");
            return Results.BadRequest(new { error = "所选地址不是当前 Windows 专用网络地址" });
        }
        selectedAddress = new Uri($"https://{parsed}:{stationOptions.LanHttpsPort}");
    }
    pairingLogger.LogInformation("已创建一次性配对会话");
    return Results.Ok(sessions.Create(requestAddress, selectedAddress));
});

app.MapPost("/device/v1/pair", async Task<IResult> (
    PairDeviceRequest request,
    PairingSessionStore sessions,
    IPairedDeviceRegistry devices,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.PublicKey) ||
        request.PublicKey.Length > 4096 ||
        string.IsNullOrWhiteSpace(request.Name) ||
        request.Name.Length > 100 ||
        request.Token.Length > 256 ||
        request.Roles.Count > 10 ||
        request.Scopes.Count > 10)
    {
        pairingLogger.LogWarning("手机提交的配对资料未通过长度或完整性检查");
        return Results.BadRequest(new { error = "配对资料为空或超过安全长度，请重新打开手机端后再试" });
    }
    if (!sessions.TryConsume(request.SessionId, request.Token, out _))
    {
        pairingLogger.LogWarning("手机提交了无效、已使用或已过期的配对会话");
        return Results.Json(
            new { error = "配对二维码已过期、已使用或不属于当前工位主机，请在电脑端刷新二维码" },
            statusCode: StatusCodes.Status401Unauthorized);
    }
    var allowedScopes = new HashSet<string>(StringComparer.Ordinal)
    {
        "scan:send",
        "camera:publish",
        "records:read",
        "video:read"
    };
    var allowedRoles = new HashSet<string>(StringComparer.Ordinal)
    {
        "scanner",
        "camera",
        "remote"
    };
    var registration = new DeviceRegistration(
        request.Name,
        request.PublicKey,
        request.Roles.Where(allowedRoles.Contains).Distinct(StringComparer.Ordinal).ToArray(),
        request.Scopes.Where(allowedScopes.Contains).Distinct(StringComparer.Ordinal).ToArray());
    var pairedDevice = await devices.PairAsync(registration, cancellationToken);
    pairingLogger.LogInformation(
        "手机配对成功，角色数量 {RoleCount}，权限数量 {ScopeCount}",
        registration.Roles.Count,
        registration.Scopes.Count);
    return Results.Ok(pairedDevice);
}).RequireRateLimiting("pairing");

app.MapGet("/api/v1/devices", async Task<IResult> (
    HttpRequest request,
    IPairedDeviceRegistry devices,
    CancellationToken cancellationToken) =>
    IsLoopback(request.HttpContext)
        ? Results.Ok(await devices.GetAllAsync(cancellationToken))
        : Results.Unauthorized());
app.MapPost("/api/v1/devices/{id}/revoke", async Task<IResult> (
    string id,
    HttpRequest request,
    IPairedDeviceRegistry devices,
    IMediaRelayManager mediaRelay,
    IClock clock,
    CancellationToken cancellationToken) =>
{
    if (!IsLoopback(request.HttpContext))
    {
        return Results.Unauthorized();
    }
    return await RemovePairedDeviceAsync(id, devices, mediaRelay, clock, cancellationToken)
        ? Results.NoContent()
        : Results.NotFound();
});
app.MapDelete("/api/v1/devices/{id}", async Task<IResult> (
    string id,
    HttpRequest request,
    IPairedDeviceRegistry devices,
    IMediaRelayManager mediaRelay,
    IClock clock,
    CancellationToken cancellationToken) =>
{
    if (!IsLoopback(request.HttpContext))
    {
        return Results.Unauthorized();
    }
    return await RemovePairedDeviceAsync(id, devices, mediaRelay, clock, cancellationToken)
        ? Results.NoContent()
        : Results.NotFound();
});

app.MapPost("/api/v1/media/publish-session", async Task<IResult> (
    HttpRequest request,
    IPairedDeviceRegistry devices,
    IMediaRelayManager mediaRelay,
    IRecordingBackend recordingBackend,
    CancellationToken cancellationToken) =>
{
    var authorization = ReadDeviceAuthorization(request);
    if (authorization is null)
    {
        return Results.Unauthorized();
    }
    var device = await devices.AuthenticateAsync(
        authorization.Value.DeviceId,
        authorization.Value.AccessToken,
        "camera:publish",
        cancellationToken);
    if (device is null)
    {
        return Results.Unauthorized();
    }
    await mediaRelay.StartAsync(cancellationToken);
    var endpoint = mediaRelay.CreatePublishEndpoint(GetAdvertisedHost(stationOptions), device.Id);
    if (recordingBackend is OpenCvRecordingBackend openCvBackend)
    {
        var phoneCamera = new CameraOptions
        {
            SourceKind = CameraSourceKind.NetworkStream,
            NetworkStreamUrl = endpoint.RtspUrl.ToString(),
            NetworkUsername = device.Id,
            NetworkPasswordProtected = CameraCredentialProtector.Protect(authorization.Value.AccessToken),
            Width = 1080,
            Height = 1920,
            FramesPerSecond = 15,
            Codec = localSettings.Camera.Codec,
            Brightness = localSettings.Camera.Brightness,
            Contrast = localSettings.Camera.Contrast,
            Sharpness = localSettings.Camera.Sharpness,
            Saturation = localSettings.Camera.Saturation,
            AutoFocus = true
        };
        await openCvBackend.ConfigureCameraAsync(phoneCamera, restartPreview: false, cancellationToken);
    }
    return Results.Ok(endpoint);
}).RequireRateLimiting("device");

app.MapGet("/api/v1/stations/{id}/live", async Task<IResult> (
    string id,
    string? deviceId,
    HttpRequest request,
    IPairedDeviceRegistry devices,
    IMediaRelayManager mediaRelay,
    CancellationToken cancellationToken) =>
{
    if (!string.Equals(id, stationOptions.StationId, StringComparison.OrdinalIgnoreCase))
    {
        return Results.NotFound();
    }
    var authorization = ReadDeviceAuthorization(request);
    if (authorization is null)
    {
        return Results.Unauthorized();
    }
    var reader = await devices.AuthenticateAsync(
        authorization.Value.DeviceId,
        authorization.Value.AccessToken,
        "video:read",
        cancellationToken);
    if (reader is null)
    {
        return Results.Unauthorized();
    }
    var allDevices = await devices.GetAllAsync(cancellationToken);
    var camera = allDevices
        .Where(item => !item.IsRevoked && item.Scopes.Contains("camera:publish", StringComparer.Ordinal))
        .Where(item => string.IsNullOrWhiteSpace(deviceId) || string.Equals(item.Id, deviceId, StringComparison.Ordinal))
        .OrderByDescending(item => item.LastSeenAt ?? item.PairedAt)
        .FirstOrDefault();
    if (camera is null)
    {
        return Results.NotFound(new { error = "没有可用的手机摄像头" });
    }
    await mediaRelay.StartAsync(cancellationToken);
    return Results.Ok(mediaRelay.CreateLiveEndpoint(
        GetAdvertisedHost(stationOptions),
        camera.Id,
        reader.Id));
}).RequireRateLimiting("device");

app.MapPost("/internal/media/auth", async Task<IResult> (
    MediaMtxAuthRequest request,
    HttpContext context,
    IPairedDeviceRegistry devices,
    CancellationToken cancellationToken) =>
{
    if (!IsLoopback(context))
    {
        return Results.Unauthorized();
    }
    var scope = request.Action switch
    {
        "publish" => "camera:publish",
        "read" => "video:read",
        _ => string.Empty
    };
    var secret = string.IsNullOrWhiteSpace(request.Password) ? request.Token : request.Password;
    if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(secret))
    {
        return Results.Unauthorized();
    }
    var device = await devices.AuthenticateAsync(request.User, secret, scope, cancellationToken);
    if (device is null || request.Action == "publish" &&
        !string.Equals(request.Path, $"device/{device.Id}", StringComparison.Ordinal))
    {
        return Results.Unauthorized();
    }
    return Results.Ok();
});
try
{
    await app.RunAsync();
}
finally
{
    DiagnosticLog.Information("工位主机正在停止");
    DiagnosticLog.CloseAndFlush();
}

static string GetEndpointTemplate(HttpContext context) =>
    context.GetEndpoint() is RouteEndpoint routeEndpoint
        ? routeEndpoint.RoutePattern.RawText ?? "unknown"
        : "unmatched";
