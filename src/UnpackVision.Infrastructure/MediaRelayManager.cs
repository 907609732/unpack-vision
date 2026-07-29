using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

public sealed class MediaRelayManager : IMediaRelayManager
{
    private static readonly HttpClient HealthClient = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly MediaRelayOptions _options;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private Process? _process;
    private volatile bool _desiredRunning;
    private int _restartScheduled;
    private string? _lastError;

    public MediaRelayManager(MediaRelayOptions options) => _options = options;

    public bool IsRunning => _process is { HasExited: false };

    public async Task<MediaRelayStatus> StartAsync(CancellationToken cancellationToken = default)
    {
        _desiredRunning = true;
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (IsRunning && await IsHealthyAsync(cancellationToken))
            {
                return CurrentStatus();
            }

            DisposeProcess();
            var executable = LocateExecutable();
            Directory.CreateDirectory(_options.RuntimeDirectory);
            var configurationPath = Path.Combine(_options.RuntimeDirectory, "mediamtx.yml");
            await File.WriteAllTextAsync(
                configurationPath,
                MediaRelayConfiguration.Build(_options),
                new UTF8Encoding(false),
                cancellationToken);

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    WorkingDirectory = _options.RuntimeDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                },
                EnableRaisingEvents = true
            };
            process.StartInfo.ArgumentList.Add(configurationPath);
            process.OutputDataReceived += (_, eventArgs) => TraceRelay(eventArgs.Data);
            process.ErrorDataReceived += (_, eventArgs) => TraceRelay(eventArgs.Data);
            process.Exited += ProcessOnExited;
            if (!process.Start())
            {
                throw new InvalidOperationException("MediaMTX 进程未能启动");
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;

            for (var attempt = 0; attempt < 30; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    throw new InvalidOperationException($"MediaMTX 启动后立即退出，退出码 {process.ExitCode}");
                }
                if (await IsHealthyAsync(cancellationToken))
                {
                    _lastError = null;
                    return CurrentStatus();
                }
                await Task.Delay(200, cancellationToken);
            }
            throw new TimeoutException("MediaMTX 健康检查超时，请检查端口 8554、8889 和 9997 是否被占用");
        }
        catch (Exception exception)
        {
            _lastError = exception.Message;
            _desiredRunning = false;
            if (_process is { HasExited: false } failedProcess)
            {
                failedProcess.Kill(entireProcessTree: true);
                await failedProcess.WaitForExitAsync(CancellationToken.None);
            }
            DisposeProcess();
            throw;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _desiredRunning = false;
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (_process is { HasExited: false } process)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken);
            }
            DisposeProcess();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task DisconnectDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        _ = CreateDevicePath(deviceId);
        if (!IsRunning)
        {
            return;
        }

        var streamPath = $"device/{deviceId}";
        try
        {
            await KickSessionsAsync("rtspsessions", streamPath, cancellationToken);
            await KickSessionsAsync("webrtcsessions", streamPath, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            TraceRelay($"无法单独断开设备 {deviceId}，将重启媒体中继：{exception.Message}");
            await StopAsync(cancellationToken);
            await StartAsync(cancellationToken);
        }
    }

    public MediaPublishEndpoint CreatePublishEndpoint(string host, string deviceId)
    {
        var path = CreateDevicePath(deviceId);
        return new MediaPublishEndpoint(path, new Uri($"rtsps://{FormatHost(host)}:{_options.RtspsPort}/{path}"), deviceId);
    }

    public MediaLiveEndpoint CreateLiveEndpoint(string host, string deviceId, string authUser)
    {
        var path = CreateDevicePath(deviceId);
        return new MediaLiveEndpoint(path, new Uri($"https://{FormatHost(host)}:{_options.WebRtcPort}/{path}/whep"), authUser);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifecycle.Dispose();
    }

    private async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await HealthClient.GetAsync(
                $"{_options.ControlApiAddress.TrimEnd('/')}/v3/info",
                cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task KickSessionsAsync(
        string resource,
        string streamPath,
        CancellationToken cancellationToken)
    {
        var root = _options.ControlApiAddress.TrimEnd('/');
        using var response = await HealthClient.GetAsync($"{root}/v3/{resource}/list", cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idNode) ||
                !item.TryGetProperty("path", out var pathNode) ||
                !string.Equals(pathNode.GetString(), streamPath, StringComparison.Ordinal))
            {
                continue;
            }
            var id = idNode.GetString();
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }
            using var kick = await HealthClient.PostAsync(
                $"{root}/v3/{resource}/kick/{Uri.EscapeDataString(id)}",
                null,
                cancellationToken);
            kick.EnsureSuccessStatusCode();
        }
    }

    private MediaRelayStatus CurrentStatus() => new(IsRunning, _options.Version, _lastError);

    private string LocateExecutable()
    {
        var candidates = new[]
        {
            Environment.ExpandEnvironmentVariables(_options.ExecutablePath),
            Path.Combine(AppContext.BaseDirectory, "MediaMTX", "mediamtx.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "mediamtx", _options.Version, "mediamtx.exe"))
        };
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            ?? throw new FileNotFoundException(
                $"未找到 MediaMTX {_options.Version}，请先运行 scripts/fetch-mediamtx.ps1");
    }

    private void OnUnexpectedExit()
    {
        if (!_desiredRunning || Interlocked.Exchange(ref _restartScheduled, 1) != 0)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1000);
                if (_desiredRunning)
                {
                    await StartAsync();
                }
            }
            catch (Exception exception)
            {
                _lastError = exception.Message;
            }
            finally
            {
                Interlocked.Exchange(ref _restartScheduled, 0);
            }
        });
    }

    private void DisposeProcess()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null)
        {
            return;
        }
        process.Exited -= ProcessOnExited;
        process.Dispose();
    }

    private void ProcessOnExited(object? sender, EventArgs eventArgs) => OnUnexpectedExit();

    private static void TraceRelay(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            var safeMessage = message
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace('\t', ' ');
            if (safeMessage.Length > 512)
            {
                safeMessage = safeMessage[..512];
            }
            Trace.WriteLine($"[MediaMTX] {safeMessage}");
        }
    }

    private static string CreateDevicePath(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("设备 ID 只能包含字母、数字、短横线和下划线", nameof(deviceId));
        }
        return $"device/{deviceId}";
    }

    private static string FormatHost(string host) =>
        host.Contains(':', StringComparison.Ordinal) && !host.StartsWith("[", StringComparison.Ordinal)
            ? $"[{host}]"
            : host;
}

public static class MediaRelayConfiguration
{
    public static string Build(MediaRelayOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.CertificatePath) ||
            string.IsNullOrWhiteSpace(options.PrivateKeyPath))
        {
            throw new InvalidOperationException("媒体中继缺少 TLS 证书配置");
        }
        var certificate = options.CertificatePath.Replace('\\', '/');
        var privateKey = options.PrivateKeyPath.Replace('\\', '/');
        return $$"""
        logLevel: info
        logDestinations: [stdout]
        authMethod: http
        authHTTPAddress: {{options.AuthHttpAddress}}
        authHTTPExclude:
        - action: api
        - action: metrics
        - action: pprof
        api: true
        apiAddress: 127.0.0.1:{{new Uri(options.ControlApiAddress).Port}}
        metrics: false
        pprof: false
        playback: false
        rtsp: true
        rtspTransports: [tcp]
        rtspEncryption: "strict"
        rtspsAddress: :{{options.RtspsPort}}
        rtspServerKey: '{{privateKey}}'
        rtspServerCert: '{{certificate}}'
        rtmp: false
        hls: false
        webrtc: true
        webrtcAddress: :{{options.WebRtcPort}}
        webrtcEncryption: true
        webrtcServerKey: '{{privateKey}}'
        webrtcServerCert: '{{certificate}}'
        webrtcAllowOrigins: []
        webrtcLocalUDPAddress: :8189
        webrtcLocalTCPAddress: ""
        webrtcIPsFromInterfaces: true
        srt: false
        paths:
          ~^device/[A-Za-z0-9_-]+$:
            source: publisher
            overridePublisher: false
        """;
    }
}
