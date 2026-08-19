using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using OpenCvSharp;
using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

/// <summary>
/// Gives every camera pipeline exclusive ownership of its encoder. Implementations are
/// deliberately synchronous at the frame boundary: backpressure is contained by the
/// owning camera task and can never block another route.
/// </summary>
internal interface IRecordingFrameWriter : IDisposable
{
    string Codec { get; }
    string EncoderName { get; }
    bool HardwareAccelerated { get; }
    void Write(Mat frame);
    void Complete();
    void Abort();
}

internal sealed class OpenCvRecordingFrameWriter : IRecordingFrameWriter
{
    private readonly VideoWriter _writer;
    private bool _completed;

    public OpenCvRecordingFrameWriter(string path, string codecName, double framesPerSecond, Size size)
    {
        var codec = (string.IsNullOrWhiteSpace(codecName) ? "mp4v" : codecName).PadRight(4, ' ').AsSpan(0, 4);
        _writer = new VideoWriter(
            path,
            VideoWriter.FourCC(codec[0], codec[1], codec[2], codec[3]),
            Math.Max(1, framesPerSecond),
            size);
        if (!_writer.IsOpened())
        {
            _writer.Dispose();
            throw new InvalidOperationException($"无法创建 {size.Width}×{size.Height} MP4编码器（{codecName}）");
        }
        Codec = codecName;
    }

    public string Codec { get; }
    public string EncoderName => "OpenCV compatibility writer";
    public bool HardwareAccelerated => false;

    public void Write(Mat frame) => _writer.Write(frame);

    public void Complete()
    {
        if (_completed) return;
        _completed = true;
        _writer.Release();
        _writer.Dispose();
    }

    public void Abort() => Complete();

    public void Dispose() => Complete();
}

/// <summary>
/// Streams already-watermarked BGR frames to one bounded gst-launch process. Closing
/// stdin sends EOS; gst-launch -e then finalizes the MP4 index before the temporary file
/// is eligible for its atomic rename.
/// </summary>
internal sealed partial class GStreamerRecordingFrameWriter : IRecordingFrameWriter
{
    private const int MaximumDiagnosticCharacters = 4096;
    private static readonly TimeSpan DefaultWriteTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FinalizationTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ProcessTerminationTimeout = TimeSpan.FromSeconds(2);
    private readonly Process _process;
    private readonly Stream _input;
    private readonly Size _expectedSize;
    private readonly string _outputPath;
    private readonly StringBuilder _diagnostics = new();
    private readonly object _diagnosticSync = new();
    private readonly byte[] _frameBuffer;
    private readonly TimeSpan _writeTimeout;
    private bool _completed;

    public GStreamerRecordingFrameWriter(
        string launchExecutablePath,
        MediaEncoderCapability encoder,
        string outputPath,
        double framesPerSecond,
        Size size,
        TimeSpan? writeTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launchExecutablePath);
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (size.Width <= 0 || size.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "录像画面尺寸必须大于零。");
        }
        if (size.Width % 2 != 0 || size.Height % 2 != 0)
        {
            throw new ArgumentException("H.264 录像画面的宽和高必须是偶数。", nameof(size));
        }

        _expectedSize = size;
        _writeTimeout = writeTimeout ?? DefaultWriteTimeout;
        if (_writeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(writeTimeout), "写入超时必须大于零。");
        }
        _outputPath = Path.GetFullPath(outputPath);
        var fps = Math.Max(1, (int)Math.Round(framesPerSecond));
        var processStart = new ProcessStartInfo
        {
            FileName = launchExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in BuildArguments(encoder, _outputPath, fps, size))
        {
            processStart.ArgumentList.Add(argument);
        }

        _process = new Process { StartInfo = processStart, EnableRaisingEvents = true };
        _process.OutputDataReceived += CaptureDiagnostic;
        _process.ErrorDataReceived += CaptureDiagnostic;
        if (!_process.Start())
        {
            _process.Dispose();
            throw new InvalidOperationException("GStreamer 编码进程无法启动。");
        }
        try
        {
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            _input = _process.StandardInput.BaseStream;
            _frameBuffer = new byte[checked(size.Width * size.Height * 3)];
        }
        catch
        {
            TerminateProcess();
            _process.Dispose();
            throw;
        }
        Codec = "H.264";
        EncoderName = encoder.ElementName;
        HardwareAccelerated = encoder.HardwareAccelerated;
    }

    public string Codec { get; }
    public string EncoderName { get; }
    public bool HardwareAccelerated { get; }

    internal static IReadOnlyList<string> BuildArguments(
        MediaEncoderCapability encoder,
        string outputPath,
        int framesPerSecond,
        Size size)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (size.Width <= 0 || size.Height <= 0 || size.Width % 2 != 0 || size.Height % 2 != 0)
        {
            throw new ArgumentException("H.264 录像画面的宽和高必须是正偶数。", nameof(size));
        }

        framesPerSecond = Math.Max(1, framesPerSecond);
        var frameBytes = checked(size.Width * size.Height * 3);
        var encoderFormat = GetEncoderInputFormat(encoder);
        // gst-launch parses backslashes as escape characters even though ArgumentList
        // already protects whitespace. Forward slashes preserve absolute Windows paths.
        var gstreamerOutputPath = Path.GetFullPath(outputPath).Replace('\\', '/');
        return
        [
            "-q", "-e",
            "fdsrc", "fd=0", $"blocksize={frameBytes}",
            "!", "videoparse", "format=bgr", $"width={size.Width}", $"height={size.Height}", $"framerate={framesPerSecond}/1",
            // A recording branch must apply backpressure rather than silently discard
            // frames. Isolation is provided by the per-camera writer task, not a leaky queue.
            "!", "queue", "max-size-buffers=3", "max-size-bytes=0", "max-size-time=0",
            "!", "videoconvert",
            "!", $"video/x-raw,format={encoderFormat}",
            "!", encoder.ElementName,
            "!", "h264parse", "config-interval=-1",
            "!", "mp4mux", "faststart=true", "fragment-duration=1000",
            "!", "filesink", $"location={gstreamerOutputPath}", "sync=false"
        ];
    }

    public void Write(Mat frame)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        if (_process.HasExited)
        {
            throw new IOException($"GStreamer 编码器已退出（{_process.ExitCode}）：{ReadDiagnostics()}");
        }
        if (frame.Type() != MatType.CV_8UC3)
        {
            throw new InvalidDataException("GStreamer 写入器只接受 BGR 三通道画面。");
        }
        if (frame.Width != _expectedSize.Width || frame.Height != _expectedSize.Height)
        {
            throw new InvalidDataException(
                $"GStreamer 写入画面尺寸必须为 {_expectedSize.Width}×{_expectedSize.Height}，" +
                $"实际为 {frame.Width}×{frame.Height}。");
        }

        Mat? continuous = null;
        var source = frame;
        if (!frame.IsContinuous())
        {
            continuous = frame.Clone();
            source = continuous;
        }
        try
        {
            var length = checked(source.Rows * source.Cols * 3);
            // The constructor and dimension check keep this buffer stable for the whole
            // stream. A changing frame size would corrupt videoparse frame boundaries.
            Marshal.Copy(source.Data, _frameBuffer, 0, length);
            WriteWithHardTimeout(
                _input,
                _frameBuffer.AsMemory(0, length),
                _writeTimeout,
                TerminateProcess);
        }
        finally
        {
            continuous?.Dispose();
        }
    }

    public void Complete()
    {
        if (_completed) return;
        _completed = true;
        Exception? inputFailure = null;
        try
        {
            try
            {
                _input.Flush();
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                inputFailure = exception;
            }
            finally
            {
                try
                {
                    _input.Dispose();
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException)
                {
                    inputFailure ??= exception;
                }
            }

            if (!_process.WaitForExit((int)FinalizationTimeout.TotalMilliseconds))
            {
                TerminateProcess();
                throw new TimeoutException("GStreamer 编码器在20秒内未完成 MP4 索引。");
            }
            // WaitForExit(timeout) does not guarantee that asynchronous output handlers
            // have drained. The parameterless wait is immediate after exit and completes them.
            _process.WaitForExit();
            if (_process.ExitCode != 0)
            {
                throw new IOException($"GStreamer 编码失败（{_process.ExitCode}）：{ReadDiagnostics()}");
            }
            if (inputFailure is not null)
            {
                throw new IOException(
                    $"GStreamer 输入流提前关闭：{ReadDiagnostics()}",
                    inputFailure);
            }
        }
        finally
        {
            if (!_process.HasExited)
            {
                TerminateProcess();
            }
            _process.Dispose();
        }
    }

    public void Dispose()
    {
        try { Complete(); } catch { }
    }

    public void Abort()
    {
        if (_completed) return;
        _completed = true;
        TerminateProcess();
        try { _input.Dispose(); } catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
        _process.Dispose();
    }

    internal static void WriteWithHardTimeout(
        Stream destination,
        ReadOnlyMemory<byte> buffer,
        TimeSpan timeout,
        Action terminate)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(terminate);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        var writeTask = destination.WriteAsync(buffer).AsTask();
        var winner = Task.WhenAny(writeTask, Task.Delay(timeout)).GetAwaiter().GetResult();
        if (!ReferenceEquals(winner, writeTask))
        {
            terminate();
            _ = writeTask.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw new TimeoutException($"GStreamer 编码器管道写入超过 {timeout.TotalSeconds:0.##} 秒，已终止该编码进程。");
        }

        writeTask.GetAwaiter().GetResult();
    }

    private void CaptureDiagnostic(object sender, DataReceivedEventArgs eventArgs)
    {
        if (string.IsNullOrWhiteSpace(eventArgs.Data)) return;
        lock (_diagnosticSync)
        {
            var remaining = MaximumDiagnosticCharacters - _diagnostics.Length;
            if (remaining <= 0) return;
            _diagnostics.AppendLine(eventArgs.Data[..Math.Min(remaining, eventArgs.Data.Length)]);
        }
    }

    private string ReadDiagnostics()
    {
        lock (_diagnosticSync)
        {
            if (_diagnostics.Length == 0)
            {
                return "没有诊断输出";
            }

            // GStreamer errors can echo filesink locations. Keep diagnostics useful while
            // preventing full business paths from entering logs or exception telemetry.
            var sanitized = _diagnostics.ToString()
                .Replace(_outputPath, "<recording-path>", StringComparison.OrdinalIgnoreCase);
            var outputDirectory = Path.GetDirectoryName(_outputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                sanitized = sanitized.Replace(
                    outputDirectory,
                    "<recording-directory>",
                    StringComparison.OrdinalIgnoreCase);
            }
            sanitized = UriCredentialRegex().Replace(sanitized, "$1://<credentials>@");
            return sanitized.Trim();
        }
    }

    private static string GetEncoderInputFormat(MediaEncoderCapability encoder) =>
        encoder.ElementName.Equals("openh264enc", StringComparison.OrdinalIgnoreCase) ||
        encoder.ElementName.Equals("x264enc", StringComparison.OrdinalIgnoreCase)
            ? "I420"
            : "NV12";

    private void TerminateProcess()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the HasExited check and Kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Cleanup is best-effort if process control is denied by Windows.
        }

        try
        {
            _process.WaitForExit((int)ProcessTerminationTimeout.TotalMilliseconds);
        }
        catch (InvalidOperationException)
        {
            // The process was never started or has already been disposed.
        }
    }

    [GeneratedRegex(@"(?i)\b(https?|rtsps?)://[^\s/@:]+:[^\s/@]+@", RegexOptions.CultureInvariant)]
    private static partial Regex UriCredentialRegex();
}
