using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnpackVision.App;

internal static class StationHostConnection
{
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
        if (await IsHealthyAsync(cancellationToken))
        {
            return;
        }

        var executable = LocateExecutable()
            ?? throw new FileNotFoundException("未找到“电商拆包智能录像工位主机.exe”，请重新安装完整程序。");
        Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = "--StationHost:LanHttpPrototypeEnabled=true",
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });

        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(250, cancellationToken);
            if (await IsHealthyAsync(cancellationToken))
            {
                return;
            }
        }
        throw new InvalidOperationException("工位主机启动超时，请检查 5271 端口是否被占用");
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

    private static async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync("/api/v1/health", cancellationToken);
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

    private static string? LocateExecutable()
    {
        var installed = Path.Combine(AppContext.BaseDirectory, "StationHost", "电商拆包智能录像工位主机.exe");
        if (File.Exists(installed))
        {
            return installed;
        }
        var development = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "UnpackVision.StationHost", "bin", "Release", "net10.0-windows", "电商拆包智能录像工位主机.exe"));
        return File.Exists(development) ? development : null;
    }
}
