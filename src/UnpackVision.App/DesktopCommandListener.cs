using System.Net;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnpackVision.Core;

namespace UnpackVision.App;

internal sealed class DesktopCommandListener(
    Func<ScanCommand, CancellationToken, Task<ScanAcknowledgement>> routeCommand,
    Func<StationStateSnapshot> getState) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _loop;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_listener.Prefixes.Count == 0)
        {
            _listener.Prefixes.Add("http://127.0.0.1:5272/");
        }

        HttpListenerException? lastError = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _listener.Start();
                _loop = Task.Run(() => RunAsync(_lifetime.Token));
                return;
            }
            catch (HttpListenerException exception)
            {
                lastError = exception;
                await Task.Delay(250, cancellationToken);
            }
        }

        throw new InvalidOperationException(
            "桌面手机命令通道启动失败，请检查本机 5272 端口是否被其他程序占用。",
            lastError);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            _ = ProcessAsync(context, cancellationToken);
        }
    }

    private async Task ProcessAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            object response;
            if (context.Request.HttpMethod == "GET" && context.Request.Url?.AbsolutePath == "/state")
            {
                response = getState();
            }
            else if (context.Request.HttpMethod == "POST" && context.Request.Url?.AbsolutePath == "/device-command")
            {
                var command = await JsonSerializer.DeserializeAsync<ScanCommand>(
                    context.Request.InputStream,
                    JsonOptions,
                    cancellationToken) ?? throw new InvalidDataException("手机命令内容为空");
                response = await routeCommand(command, cancellationToken);
            }
            else
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                context.Response.Close();
                return;
            }

            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "application/json; charset=utf-8";
            await JsonSerializer.SerializeAsync(context.Response.OutputStream, response, JsonOptions, cancellationToken);
        }
        catch (Exception exception)
        {
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            context.Response.ContentType = "application/json; charset=utf-8";
            await JsonSerializer.SerializeAsync(
                context.Response.OutputStream,
                new { error = exception.Message },
                JsonOptions,
                CancellationToken.None);
        }
        finally
        {
            context.Response.Close();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _listener.Stop();
        if (_loop is not null)
        {
            try { await _loop; }
            catch (OperationCanceledException) { }
        }
        _listener.Close();
        _lifetime.Dispose();
    }
}
