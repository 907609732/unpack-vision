using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnpackVision.Infrastructure.Diagnostics;

namespace UnpackVision.App;

internal sealed record StationHostHealthSnapshot(
    string Status,
    string Version,
    bool Tls,
    string[] LanAddresses);

internal static class StationHostConnection
{
    private static readonly TimeSpan HealthProbeTimeout = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    internal static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:5271"),
        Timeout = TimeSpan.FromSeconds(8)
    };

    internal static async Task EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        DiagnosticLog.Information("开始检查工位主机状态");
        if (await IsHealthyAsync(cancellationToken))
        {
            DiagnosticLog.Information(
                "工位主机已在运行，检查耗时 {ElapsedMilliseconds} 毫秒",
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return;
        }

        var executable = LocateExecutable()
            ?? throw new FileNotFoundException("未找到“拆包智录工位主机.exe”，请重新安装完整程序。");
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = "--StationHost:LanHttpPrototypeEnabled=false --StationHost:LanHttpsEnabled=true",
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        }) ?? throw new InvalidOperationException("无法启动工位主机进程");
        DiagnosticLog.Information("已请求启动工位主机进程，进程号 {ChildProcessId}", process.Id);

        using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupCancellation.CancelAfter(StartupTimeout);
        try
        {
            var attempt = 0;
            while (true)
            {
                startupCancellation.Token.ThrowIfCancellationRequested();
                attempt++;
                await Task.Delay(250, startupCancellation.Token);
                if (await IsHealthyAsync(startupCancellation.Token))
                {
                    DiagnosticLog.Information(
                        "工位主机启动成功，尝试 {AttemptCount} 次，耗时 {ElapsedMilliseconds} 毫秒",
                        attempt,
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            DiagnosticLog.Warning(
                "工位主机在 {TimeoutSeconds} 秒内没有通过健康检查",
                StartupTimeout.TotalSeconds);
            throw new TimeoutException("工位主机启动超时，请检查 5271 端口是否被占用");
        }
    }

    internal static async Task StopAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await Http.PostAsync("/internal/shutdown", null, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }
            for (var attempt = 0; attempt < 30; attempt++)
            {
                await Task.Delay(100, cancellationToken);
                if (!await IsHealthyAsync(cancellationToken))
                {
                    return;
                }
            }
        }
        catch (HttpRequestException)
        {
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    internal static async Task<IReadOnlyList<string>> SynchronizeLanBindingsAsync(
        IReadOnlyCollection<string> currentLanAddresses,
        CancellationToken cancellationToken = default)
    {
        var expected = NormalizeLanAddresses(currentLanAddresses);
        if (expected.Length == 0)
        {
            throw new InvalidOperationException("未找到可用于手机配对的 Windows 专用网络地址");
        }

        await EnsureRunningAsync(cancellationToken);
        var health = await GetHealthAsync(cancellationToken);
        if (!LanAddressSetsMatch(expected, health.LanAddresses))
        {
            DiagnosticLog.Information(
                "当前专用网络地址与工位主机监听地址不一致，正在刷新多 IP 监听");
            await StopAsync(cancellationToken);
            await EnsureRunningAsync(cancellationToken);
            health = await GetHealthAsync(cancellationToken);
        }

        var bound = NormalizeLanAddresses(health.LanAddresses);
        var usable = expected
            .Where(address => bound.Contains(address, StringComparer.Ordinal))
            .ToArray();
        if (usable.Length != expected.Length)
        {
            throw new InvalidOperationException(
                "工位主机未能监听全部专用网络地址，请断开无用网卡后重试");
        }
        return usable;
    }

    internal static bool LanAddressSetsMatch(
        IEnumerable<string>? first,
        IEnumerable<string>? second)
    {
        var left = NormalizeLanAddresses(first);
        var right = NormalizeLanAddresses(second);
        return left.Length == right.Length &&
               left.SequenceEqual(right, StringComparer.Ordinal);
    }

    private static async Task<StationHostHealthSnapshot> GetHealthAsync(
        CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync("/api/v1/health", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<StationHostHealthSnapshot>(
                   JsonOptions,
                   cancellationToken)
               ?? throw new InvalidOperationException("工位主机健康状态为空");
    }

    private static string[] NormalizeLanAddresses(IEnumerable<string>? addresses) =>
        addresses?
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Select(address => address.Trim())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray()
        ?? [];

    private static async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCancellation.CancelAfter(HealthProbeTimeout);
        try
        {
            using var response = await Http.GetAsync("/api/v1/health", probeCancellation.Token);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static string? LocateExecutable()
    {
        var installed = Path.Combine(AppContext.BaseDirectory, "StationHost", "拆包智录工位主机.exe");
        if (File.Exists(installed))
        {
            return installed;
        }
        var development = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "UnpackVision.StationHost", "bin", "Release", "net10.0-windows", "拆包智录工位主机.exe"));
        return File.Exists(development) ? development : null;
    }
}
