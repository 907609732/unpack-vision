using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Net.Http.Headers;
using UnpackVision.Core;
using UnpackVision.Infrastructure;
using UnpackVision.StationHost;

using var hostInstanceMutex = new Mutex(
    true,
    @"Local\UnpackVision.StationHost.SingleInstance",
    out var isFirstHostInstance);
if (!isFirstHostInstance)
{
    return;
}

var settingsStore = new LocalSettingsStore();
var localSettings = await settingsStore.LoadAsync();
var builder = WebApplication.CreateBuilder(args);
var storageOptions = BindOptions<StorageOptions>(builder.Configuration, "Storage");
var excelOptions = BindOptions<ExcelConnectorOptions>(builder.Configuration, "Excel");
var stationOptions = BindOptions<StationHostOptions>(builder.Configuration, "StationHost");
var mediaRelayOptions = BindOptions<MediaRelayOptions>(builder.Configuration, "MediaRelay");
if (string.IsNullOrWhiteSpace(stationOptions.StationId))
{
    stationOptions.StationId = Environment.MachineName;
}
if (stationOptions.LanHttpPrototypeEnabled)
{
    // Development-only bridge for the first physical-phone prototype. The
    // default remains loopback-only; production pairing will use pinned TLS.
    // Bind only the addresses the station actually uses. Binding 0.0.0.0 can
    // fail when another desktop app already owns the same source port on an
    // unrelated virtual adapter (for example iCloud or a proxy adapter).
    builder.WebHost.UseUrls(GetLanPrototypeUrls());
}

builder.Services.AddSingleton(storageOptions);
builder.Services.AddSingleton(excelOptions);
builder.Services.AddSingleton(stationOptions);
builder.Services.AddSingleton(mediaRelayOptions);
builder.Services.AddSingleton(localSettings);
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IScanRecordRepository, SqliteScanRecordRepository>();
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
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();
var repository = app.Services.GetRequiredService<IScanRecordRepository>();
await repository.InitializeAsync();
var deviceRegistry = app.Services.GetRequiredService<IPairedDeviceRegistry>();
await deviceRegistry.InitializeAsync();
var commandLedger = app.Services.GetRequiredService<IScanCommandLedger>();
await commandLedger.InitializeAsync();

app.MapOpenApi();
app.MapGet("/api/v1/health", (IScanCommandRouter router, IMediaRelayManager mediaRelay) => Results.Ok(new
{
    status = "healthy",
    station = router.GetState(),
    mediaRelayRunning = mediaRelay.IsRunning,
    pairingTlsReady = !string.IsNullOrWhiteSpace(stationOptions.CertificateFingerprint),
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
    if (record?.VideoPath is not { Length: > 0 } videoPath || !File.Exists(videoPath))
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
    var snapshot = record?.Snapshots.FirstOrDefault(File.Exists);
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
});

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
            ? Results.Json(new { error = "电脑桌面端未就绪，请先打开电商拆包智能录像" }, statusCode: StatusCodes.Status503ServiceUnavailable)
            : Results.Ok(acknowledgement);
    }

    return Results.Ok(await router.RouteAsync(command, cancellationToken));
});

app.MapPost("/api/v1/pairing/sessions", (HttpRequest request, PairingSessionStore sessions) =>
{
    if (!IsLoopback(request.HttpContext))
    {
        return Results.Unauthorized();
    }
    var requestAddress = new Uri($"{request.Scheme}://{request.Host}");
    return Results.Ok(sessions.Create(requestAddress));
});

app.MapPost("/device/v1/pair", async Task<IResult> (
    PairDeviceRequest request,
    PairingSessionStore sessions,
    IPairedDeviceRegistry devices,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.PublicKey))
    {
        return Results.BadRequest(new { error = "手机设备密钥为空，请重新打开手机端后再试" });
    }
    if (!sessions.TryConsume(request.SessionId, request.Token, out _))
    {
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
    var registration = new DeviceRegistration(
        request.Name,
        request.PublicKey,
        request.Roles.Distinct(StringComparer.Ordinal).ToArray(),
        request.Scopes.Where(allowedScopes.Contains).Distinct(StringComparer.Ordinal).ToArray());
    return Results.Ok(await devices.PairAsync(registration, cancellationToken));
});

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
    var endpoint = mediaRelay.CreatePublishEndpoint(request.Host.Host, device.Id);
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
});

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
    return Results.Ok(mediaRelay.CreateLiveEndpoint(request.Host.Host, camera.Id, reader.Id));
});

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

static string[] GetLanPrototypeUrls()
{
    var urls = NetworkInterface.GetAllNetworkInterfaces()
        .Where(item => item.OperationalStatus == OperationalStatus.Up)
        .Where(item => item.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
        .SelectMany(item => item.GetIPProperties().UnicastAddresses)
        .Select(item => item.Address)
        .Where(address => address.AddressFamily == AddressFamily.InterNetwork && IsPrivateIpv4(address))
        .Select(address => $"http://{address}:5271")
        .Distinct(StringComparer.Ordinal)
        .ToList();
    urls.Insert(0, "http://127.0.0.1:5271");
    return [.. urls];
}

static bool IsPrivateIpv4(IPAddress address)
{
    var bytes = address.GetAddressBytes();
    return bytes[0] == 10 ||
           bytes[0] == 192 && bytes[1] == 168 ||
           bytes[0] == 172 && bytes[1] is >= 16 and <= 31;
}

static bool IsLoopback(HttpContext context) =>
    context.Connection.RemoteIpAddress is { } address && System.Net.IPAddress.IsLoopback(address);

static (string DeviceId, string AccessToken)? ReadDeviceAuthorization(HttpRequest request)
{
    var deviceId = request.Headers["X-UnpackVision-Device"].ToString().Trim();
    var authorization = request.Headers.Authorization.ToString();
    const string prefix = "Bearer ";
    if (string.IsNullOrWhiteSpace(deviceId) || !authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
    {
        return null;
    }
    var token = authorization[prefix.Length..].Trim();
    return string.IsNullOrWhiteSpace(token) ? null : (deviceId, token);
}

static async Task<bool> AuthorizeAsync(
    HttpContext context,
    IPairedDeviceRegistry devices,
    string scope,
    CancellationToken cancellationToken)
{
    if (IsLoopback(context))
    {
        return true;
    }
    var authorization = ReadDeviceAuthorization(context.Request);
    return authorization is not null && await devices.AuthenticateAsync(
        authorization.Value.DeviceId,
        authorization.Value.AccessToken,
        scope,
        cancellationToken) is not null;
}

static async Task<bool> RemovePairedDeviceAsync(
    string deviceId,
    IPairedDeviceRegistry devices,
    IMediaRelayManager mediaRelay,
    IClock clock,
    CancellationToken cancellationToken)
{
    if (!await devices.RevokeAsync(deviceId, clock.Now, cancellationToken))
    {
        return false;
    }
    await mediaRelay.DisconnectDeviceAsync(deviceId, cancellationToken);
    return await devices.DeleteAsync(deviceId, cancellationToken);
}

static StationRecordView ToStationRecordView(ScanRecord record)
{
    var hasVideo = record.VideoPath is { Length: > 0 } && File.Exists(record.VideoPath);
    long? videoBytes = hasVideo ? new FileInfo(record.VideoPath!).Length : null;
    double? duration = record.RecordingStartedAt is { } started && record.RecordingEndedAt is { } ended
        ? Math.Max(0, (ended - started).TotalSeconds)
        : null;
    return new StationRecordView(
        record.Id,
        record.TrackingNo,
        record.Workflow,
        record.State,
        record.ScannedAt,
        record.RecordingStartedAt,
        record.RecordingEndedAt,
        duration,
        record.Note,
        record.Tags,
        record.CameraId,
        record.StationId,
        record.DuplicateOf,
        record.PlatformMatchStatus,
        record.FailureReason,
        hasVideo,
        videoBytes,
        record.Snapshots.Any(File.Exists),
        record.UpdatedAt);
}

static IReadOnlyList<StationRecordEvent> BuildRecordEvents(
    ScanRecord record,
    IReadOnlyList<RecordTagAssignment> tags)
{
    var events = new List<StationRecordEvent>
    {
        new("record.scanned", record.ScannedAt, $"扫描单号 {record.TrackingNo}")
    };
    if (record.RecordingStartedAt is { } started)
    {
        events.Add(new("record.started", started, "开始录像"));
    }
    if (record.RecordingEndedAt is { } ended)
    {
        events.Add(new("record.completed", ended, record.State == RecordingState.Failed ? "录像失败" : "录像结束"));
    }
    foreach (var tag in tags)
    {
        events.Add(new("record.tagged", tag.TaggedAt, $"标记异常：{tag.TagName}", tag.TagId, tag.Id));
        if (tag.RemovedAt is { } removedAt)
        {
            events.Add(new("record.tag_removed", removedAt, $"撤销异常：{tag.TagName}", tag.TagId, tag.Id));
        }
    }
    if (record.NoteUpdatedAt is { } noteUpdatedAt)
    {
        events.Add(new("record.note_updated", noteUpdatedAt, "备注已更新"));
    }
    if (!string.IsNullOrWhiteSpace(record.FailureReason))
    {
        events.Add(new("record.failed", record.UpdatedAt, record.FailureReason));
    }
    return events.OrderBy(item => item.At).ToArray();
}

static bool TryDecodeCursor(string? cursor, out int offset)
{
    offset = 0;
    if (string.IsNullOrWhiteSpace(cursor))
    {
        return true;
    }
    try
    {
        var normalized = cursor.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
        var text = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out offset) && offset >= 0;
    }
    catch (FormatException)
    {
        return false;
    }
}

static string EncodeCursor(int offset) => Convert.ToBase64String(
        Encoding.UTF8.GetBytes(offset.ToString(CultureInfo.InvariantCulture)))
    .TrimEnd('=')
    .Replace('+', '-')
    .Replace('/', '_');

static string GetVideoContentType(string extension) => extension.ToLowerInvariant() switch
{
    ".webm" => "video/webm",
    ".mov" => "video/quicktime",
    ".avi" => "video/x-msvideo",
    _ => "video/mp4"
};

static string GetImageContentType(string extension) => extension.ToLowerInvariant() switch
{
    ".png" => "image/png",
    ".webp" => "image/webp",
    ".bmp" => "image/bmp",
    _ => "image/jpeg"
};
