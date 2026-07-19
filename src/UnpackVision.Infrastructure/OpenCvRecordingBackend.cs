using OpenCvSharp;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

public sealed class PreviewFrameEventArgs(byte[] jpegBytes, DateTimeOffset capturedAt, string? trackingNo) : EventArgs
{
    public byte[] JpegBytes { get; } = jpegBytes;
    public DateTimeOffset CapturedAt { get; } = capturedAt;
    public string? TrackingNo { get; } = trackingNo;
}

public sealed class CameraErrorEventArgs(Exception error) : EventArgs
{
    public Exception Error { get; } = error;
}

public sealed record CameraRuntimeInfo(int Width, int Height, double FramesPerSecond, int CameraIndex, string DisplayName);

public sealed class OpenCvRecordingBackend : IRecordingBackend
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _captureSync = new();
    private readonly object _writerSync = new();
    private readonly object _frameSync = new();
    private readonly StorageOptions _storageOptions;
    private readonly CameraOptions _cameraOptions;
    private VideoCapture? _capture;
    private VideoWriter? _writer;
    private CancellationTokenSource? _cameraCancellation;
    private Task? _captureLoop;
    private Mat? _lastFrame;
    private Exception? _captureError;
    private RecordingSession? _activeSession;
    private int _rotationQuarterTurns;
    private bool _mirror;
    private int _previewFrameCounter;
    private int _actualWidth;
    private int _actualHeight;
    private double _actualFramesPerSecond;
    private int _activeCameraIndex = -1;
    private string _activeCameraDisplayName = string.Empty;
    private bool _disposed;

    public OpenCvRecordingBackend(StorageOptions storageOptions, CameraOptions cameraOptions)
    {
        _storageOptions = storageOptions;
        _cameraOptions = cameraOptions;
    }

    public event EventHandler<PreviewFrameEventArgs>? PreviewFrameReady;
    public event EventHandler<CameraErrorEventArgs>? CameraError;

    public bool IsPreviewing => _captureLoop is { IsCompleted: false };
    public bool IsRecording => _activeSession is not null;
    public CameraRuntimeInfo? RuntimeInfo => _actualWidth > 0
        ? new CameraRuntimeInfo(_actualWidth, _actualHeight, _actualFramesPerSecond, _activeCameraIndex, _activeCameraDisplayName)
        : null;

    public async Task StartPreviewAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            EnsureCameraStartedCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestartPreviewAsync(CameraOptions cameraOptions, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_activeSession is not null)
            {
                throw new InvalidOperationException("录像过程中不能切换相机或分辨率");
            }
            CopyCameraOptions(cameraOptions, _cameraOptions);
            await StopCameraCoreAsync(cancellationToken);
            EnsureCameraStartedCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RecordingSession> StartAsync(
        Guid recordId,
        string trackingNo,
        WorkflowMode workflow,
        DateTimeOffset scannedAt,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_activeSession is not null)
            {
                throw new InvalidOperationException("相机已经在录制");
            }
            EnsureCameraStartedCore();
            if (_captureError is not null)
            {
                throw new InvalidOperationException("相机预览发生错误，请重新连接相机", _captureError);
            }

            var startedAt = DateTimeOffset.Now;
            var modeDirectory = workflow == WorkflowMode.Unpacking ? "Unpacking" : "Packing";
            var outputDirectory = Path.Combine(_storageOptions.RecordingRoot, modeDirectory);
            Directory.CreateDirectory(outputDirectory);
            var safeTracking = SanitizeFileName(trackingNo);
            var temporaryPath = Path.Combine(
                outputDirectory,
                $"{safeTracking}_{startedAt:yyyyMMddHHmmss}_{recordId:N}.partial.mp4");

            var outputSize = GetTransformedSize();
            var codec = _cameraOptions.Codec.PadRight(4, ' ').AsSpan(0, 4);
            var fourCc = VideoWriter.FourCC(codec[0], codec[1], codec[2], codec[3]);
            var writer = new VideoWriter(
                temporaryPath,
                fourCc,
                _actualFramesPerSecond > 0 ? _actualFramesPerSecond : _cameraOptions.FramesPerSecond,
                outputSize);
            if (!writer.IsOpened())
            {
                writer.Dispose();
                throw new InvalidOperationException($"无法创建 MP4 编码器（{_cameraOptions.Codec}）");
            }

            var session = new RecordingSession(recordId, trackingNo, workflow, startedAt, temporaryPath);
            lock (_writerSync)
            {
                _writer = writer;
                _activeSession = session;
            }
            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RecordingCompletion> StopAsync(
        RecordingSession session,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureActive(session);
            FinalizeWriterCore();
            var error = _captureError;
            lock (_writerSync)
            {
                _activeSession = null;
            }
            if (error is not null)
            {
                throw new InvalidOperationException("录像过程中发生相机错误", error);
            }

            var endedAt = DateTimeOffset.Now;
            var finalPath = Path.Combine(
                Path.GetDirectoryName(session.TemporaryPath)!,
                $"{SanitizeFileName(session.TrackingNo)}_{session.StartedAt:yyyyMMddHHmmss}_{endedAt:yyyyMMddHHmmss}.mp4");
            if (!File.Exists(session.TemporaryPath) || new FileInfo(session.TemporaryPath).Length == 0)
            {
                throw new InvalidDataException("录像文件为空");
            }
            File.Move(session.TemporaryPath, finalPath, false);
            return new RecordingCompletion(finalPath, endedAt);
        }
        catch
        {
            FinalizeWriterCore();
            lock (_writerSync)
            {
                _activeSession = null;
            }
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AbortAsync(RecordingSession session, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_activeSession?.RecordId != session.RecordId)
            {
                return;
            }
            FinalizeWriterCore();
            lock (_writerSync)
            {
                _activeSession = null;
            }
            TryDelete(session.TemporaryPath);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> TakeSnapshotAsync(CancellationToken cancellationToken = default)
    {
        Mat snapshot;
        lock (_frameSync)
        {
            if (_lastFrame is null || _lastFrame.Empty())
            {
                throw new InvalidOperationException("相机尚未提供有效画面");
            }
            snapshot = _lastFrame.Clone();
        }

        using (snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.Combine(_storageOptions.RecordingRoot, "Snapshots", DateTime.Now.ToString("yyyyMMdd"));
            Directory.CreateDirectory(directory);
            var tracking = _activeSession is null ? "snapshot" : SanitizeFileName(_activeSession.TrackingNo);
            var path = Path.Combine(directory, $"{tracking}_{DateTime.Now:yyyyMMddHHmmssfff}.jpg");
            if (!Cv2.ImWrite(path, snapshot))
            {
                throw new IOException("拍照保存失败");
            }
            return path;
        }
    }

    public async Task RotateLeftAsync(CancellationToken cancellationToken = default) =>
        await SetRotationAsync(-1, cancellationToken);

    public async Task RotateRightAsync(CancellationToken cancellationToken = default) =>
        await SetRotationAsync(1, cancellationToken);

    public async Task ToggleMirrorAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNotRecordingForTransform();
            _mirror = !_mirror;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ApplyImageControlsAsync(
        double brightness,
        double contrast,
        double sharpness,
        double saturation,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            lock (_captureSync)
            {
                _cameraOptions.Brightness = brightness;
                _cameraOptions.Contrast = contrast;
                _cameraOptions.Sharpness = sharpness;
                _cameraOptions.Saturation = saturation;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAutoFocusAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _cameraOptions.AutoFocus = enabled;
            lock (_captureSync)
            {
                _capture?.Set(VideoCaptureProperties.AutoFocus, enabled ? 1 : 0);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task FocusOnceAsync(CancellationToken cancellationToken = default)
    {
        await SetAutoFocusAsync(true, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            FinalizeWriterCore();
            lock (_writerSync)
            {
                _activeSession = null;
            }
            await StopCameraCoreAsync(CancellationToken.None);
            lock (_frameSync)
            {
                _lastFrame?.Dispose();
                _lastFrame = null;
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private void EnsureCameraStartedCore()
    {
        if (_captureLoop is { IsCompleted: false } && _capture is not null && _capture.IsOpened())
        {
            return;
        }

        _captureError = null;
        _capture = OpenMatchingCamera();
        _capture.Set(VideoCaptureProperties.AutoFocus, _cameraOptions.AutoFocus ? 1 : 0);

        _actualWidth = Math.Max(1, (int)_capture.Get(VideoCaptureProperties.FrameWidth));
        _actualHeight = Math.Max(1, (int)_capture.Get(VideoCaptureProperties.FrameHeight));
        _actualFramesPerSecond = _capture.Get(VideoCaptureProperties.Fps);
        if (_actualFramesPerSecond <= 0)
        {
            _actualFramesPerSecond = _cameraOptions.FramesPerSecond;
        }
        _cameraCancellation = new CancellationTokenSource();
        _captureLoop = Task.Run(() => CaptureLoop(_cameraCancellation.Token), CancellationToken.None);
    }

    private VideoCapture OpenMatchingCamera()
    {
        if (_cameraOptions.SourceKind is CameraSourceKind.NetworkStream or CameraSourceKind.HikvisionRecorder)
        {
            return OpenNetworkStream();
        }

        var indices = _cameraOptions.AutoSelectBestCamera
            ? Enumerable.Repeat(_cameraOptions.CameraIndex, 1)
                .Concat(Enumerable.Range(0, Math.Max(1, _cameraOptions.ProbeCameraCount)))
                .Distinct()
            : [_cameraOptions.CameraIndex];
        var openedAny = false;
        var foundResolutions = new List<string>();
        var bestFallbackIndex = -1;
        long bestFallbackPixels = -1;

        foreach (var index in indices)
        {
            var candidate = new VideoCapture(index, VideoCaptureAPIs.MSMF);
            if (!candidate.IsOpened())
            {
                candidate.Dispose();
                continue;
            }

            openedAny = true;
            ConfigureLocalCamera(candidate);
            var actualWidth = Math.Max(1, (int)candidate.Get(VideoCaptureProperties.FrameWidth));
            var actualHeight = Math.Max(1, (int)candidate.Get(VideoCaptureProperties.FrameHeight));
            var pixels = (long)actualWidth * actualHeight;
            if (pixels > bestFallbackPixels)
            {
                bestFallbackPixels = pixels;
                bestFallbackIndex = index;
            }

            if (!_cameraOptions.AutoSelectBestCamera || IsRequestedResolutionSatisfied(
                    actualWidth,
                    actualHeight,
                    _cameraOptions.Width,
                    _cameraOptions.Height,
                    _cameraOptions.MinimumResolutionRatio))
            {
                _activeCameraIndex = index;
                _activeCameraDisplayName = _cameraOptions.AutoSelectBestCamera
                    ? $"自动选择 · 本地相机 {index + 1}"
                    : $"本地相机 {index + 1}";
                return candidate;
            }

            foundResolutions.Add($"相机 {index + 1}: {actualWidth}×{actualHeight}");
            candidate.Release();
            candidate.Dispose();
        }

        if (_cameraOptions.AutoSelectBestCamera && bestFallbackIndex >= 0)
        {
            var fallback = new VideoCapture(bestFallbackIndex, VideoCaptureAPIs.MSMF);
            if (fallback.IsOpened())
            {
                ConfigureLocalCamera(fallback);
                _activeCameraIndex = bestFallbackIndex;
                _activeCameraDisplayName = $"自动选择 · 本地相机 {bestFallbackIndex + 1}（实际分辨率）";
                return fallback;
            }
            fallback.Dispose();
        }

        _activeCameraIndex = -1;
        var details = foundResolutions.Count == 0 ? string.Empty : $"；已探测到 {string.Join("，", foundResolutions)}";
        var reason = openedAny
            ? $"未找到满足 {_cameraOptions.Width}×{_cameraOptions.Height} 的摄像头{details}"
            : "未找到可用摄像头";
        throw new InvalidOperationException(
            $"{reason}。如果 4K USB Camera 正被 HIK SCAN 使用，请先关闭 HIK SCAN；也可在设置中选择相机序号或降低分辨率。");
    }

    private void ConfigureLocalCamera(VideoCapture candidate)
    {
        candidate.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));
        candidate.Set(VideoCaptureProperties.FrameWidth, _cameraOptions.Width);
        candidate.Set(VideoCaptureProperties.FrameHeight, _cameraOptions.Height);
        candidate.Set(VideoCaptureProperties.Fps, _cameraOptions.FramesPerSecond);
    }

    private VideoCapture OpenNetworkStream()
    {
        var url = CameraSourceUrlBuilder.Build(_cameraOptions);
        var candidate = new VideoCapture();
        if (!candidate.Open(url, VideoCaptureAPIs.FFMPEG))
        {
            candidate.Dispose();
            var sourceName = _cameraOptions.SourceKind == CameraSourceKind.HikvisionRecorder ? "海康录像机" : "网络摄像头";
            throw new InvalidOperationException($"无法连接{sourceName}视频流，请检查地址、账号、密码、RTSP 端口和通道配置");
        }
        _activeCameraIndex = -1;
        _activeCameraDisplayName = _cameraOptions.SourceKind == CameraSourceKind.HikvisionRecorder
            ? $"海康录像机 · 通道 {_cameraOptions.HikvisionChannel} · {(_cameraOptions.HikvisionSubStream ? "子码流" : "主码流")}"
            : "IPC / 网络视频流";
        return candidate;
    }

    public static bool IsRequestedResolutionSatisfied(
        int actualWidth,
        int actualHeight,
        int requestedWidth,
        int requestedHeight,
        double minimumRatio = 0.9)
    {
        if (actualWidth <= 0 || actualHeight <= 0 || requestedWidth <= 0 || requestedHeight <= 0)
        {
            return false;
        }
        var ratio = Math.Clamp(minimumRatio, 0.1, 1);
        return actualWidth >= requestedWidth * ratio && actualHeight >= requestedHeight * ratio;
    }

    private void CaptureLoop(CancellationToken cancellationToken)
    {
        using var rawFrame = new Mat();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                bool read;
                lock (_captureSync)
                {
                    read = _capture is not null && _capture.Read(rawFrame);
                }
                if (!read || rawFrame.Empty())
                {
                    throw new IOException("相机未返回有效画面");
                }

                using var frame = TransformFrame(rawFrame);
                RecordingSession? session;
                lock (_writerSync)
                {
                    session = _activeSession;
                    if (session is not null)
                    {
                        DrawRecordingWatermark(frame, session);
                    }
                    _writer?.Write(frame);
                }

                if (++_previewFrameCounter % 3 == 0)
                {
                    lock (_frameSync)
                    {
                        _lastFrame?.Dispose();
                        _lastFrame = frame.Clone();
                    }
                    PublishPreview(frame, session);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _captureError = ex;
            try
            {
                CameraError?.Invoke(this, new CameraErrorEventArgs(ex));
            }
            catch
            {
            }
        }
        finally
        {
            FinalizeWriterCore();
            lock (_captureSync)
            {
                _capture?.Release();
                _capture?.Dispose();
                _capture = null;
            }
        }
    }

    private void PublishPreview(Mat frame, RecordingSession? session)
    {
        try
        {
            using var preview = ResizeForPreview(frame, 1280, 720);
            Cv2.ImEncode(
                ".jpg",
                preview,
                out var bytes,
                [new ImageEncodingParam(ImwriteFlags.JpegQuality, 82)]);
            PreviewFrameReady?.Invoke(this, new PreviewFrameEventArgs(bytes, DateTimeOffset.Now, session?.TrackingNo));
        }
        catch
        {
        }
    }

    private Mat TransformFrame(Mat input)
    {
        var output = input.Clone();
        double brightness;
        double contrast;
        double sharpness;
        double saturation;
        lock (_captureSync)
        {
            brightness = _cameraOptions.Brightness;
            contrast = _cameraOptions.Contrast;
            sharpness = _cameraOptions.Sharpness;
            saturation = _cameraOptions.Saturation;
        }
        OpenCvFrameAdjuster.ApplyInPlace(output, brightness, contrast, sharpness, saturation);
        if (_mirror)
        {
            Cv2.Flip(output, output, FlipMode.Y);
        }
        switch ((_rotationQuarterTurns % 4 + 4) % 4)
        {
            case 1:
                Cv2.Rotate(output, output, RotateFlags.Rotate90Clockwise);
                break;
            case 2:
                Cv2.Rotate(output, output, RotateFlags.Rotate180);
                break;
            case 3:
                Cv2.Rotate(output, output, RotateFlags.Rotate90Counterclockwise);
                break;
        }
        return output;
    }

    private static Mat ResizeForPreview(Mat frame, int maximumWidth, int maximumHeight)
    {
        var scale = Math.Min(1d, Math.Min((double)maximumWidth / frame.Width, (double)maximumHeight / frame.Height));
        if (scale >= 0.999)
        {
            return frame.Clone();
        }
        var resized = new Mat();
        Cv2.Resize(frame, resized, new Size((int)(frame.Width * scale), (int)(frame.Height * scale)), interpolation: InterpolationFlags.Area);
        return resized;
    }

    private static void DrawRecordingWatermark(Mat frame, RecordingSession session)
    {
        var scale = Math.Max(0.8, frame.Width / 1920d);
        var thickness = Math.Max(2, (int)Math.Round(scale * 2));
        var x = Math.Max(18, frame.Width / 100);
        var y = Math.Max(42, frame.Height / 22);
        DrawOutlinedText(frame, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), new Point(x, y), scale, thickness);
        DrawChineseTrackingText(frame, session.TrackingNo, x, y + (int)(12 * scale), scale, thickness);
    }

    private static void DrawChineseTrackingText(Mat frame, string trackingNo, int x, int y, double scale, int thickness)
    {
        if (frame.Type() != MatType.CV_8UC3 || frame.Empty())
        {
            DrawOutlinedText(frame, $"Tracking No: {trackingNo}", new Point(x, y + (int)(28 * scale)), scale, thickness);
            return;
        }
        try
        {
            using var bitmap = new System.Drawing.Bitmap(
                frame.Width,
                frame.Height,
                checked((int)frame.Step()),
                PixelFormat.Format24bppRgb,
                frame.Data);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var family = new System.Drawing.FontFamily("Microsoft YaHei UI");
            using var path = new GraphicsPath();
            path.AddString(
                $"快递单号：{trackingNo}",
                family,
                (int)System.Drawing.FontStyle.Bold,
                (float)(27 * scale),
                new System.Drawing.PointF(x, y),
                System.Drawing.StringFormat.GenericDefault);
            using var outline = new System.Drawing.Pen(System.Drawing.Color.Black, Math.Max(3, thickness + 2))
            {
                LineJoin = LineJoin.Round
            };
            graphics.DrawPath(outline, path);
            graphics.FillPath(System.Drawing.Brushes.White, path);
            graphics.Flush();
        }
        catch (Exception ex) when (ex is ArgumentException or PlatformNotSupportedException)
        {
            DrawOutlinedText(frame, $"Tracking No: {trackingNo}", new Point(x, y + (int)(28 * scale)), scale, thickness);
        }
    }

    private static void DrawOutlinedText(Mat frame, string text, Point origin, double scale, int thickness)
    {
        Cv2.PutText(frame, text, origin, HersheyFonts.HersheySimplex, scale, Scalar.Black, thickness + 3, LineTypes.AntiAlias);
        Cv2.PutText(frame, text, origin, HersheyFonts.HersheySimplex, scale, Scalar.White, thickness, LineTypes.AntiAlias);
    }

    private async Task SetRotationAsync(int delta, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNotRecordingForTransform();
            _rotationQuarterTurns = (_rotationQuarterTurns + delta + 4) % 4;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureNotRecordingForTransform()
    {
        if (_activeSession is not null)
        {
            throw new InvalidOperationException("录像过程中不能旋转或镜像画面");
        }
    }

    private Size GetTransformedSize() => _rotationQuarterTurns % 2 == 0
        ? new Size(_actualWidth, _actualHeight)
        : new Size(_actualHeight, _actualWidth);

    private void EnsureActive(RecordingSession session)
    {
        if (_activeSession?.RecordId != session.RecordId)
        {
            throw new InvalidOperationException("录像会话已经失效");
        }
    }

    private void FinalizeWriterCore()
    {
        lock (_writerSync)
        {
            _writer?.Release();
            _writer?.Dispose();
            _writer = null;
        }
    }

    private async Task StopCameraCoreAsync(CancellationToken cancellationToken)
    {
        _cameraCancellation?.Cancel();
        if (_captureLoop is not null)
        {
            try
            {
                await _captureLoop.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }
        _cameraCancellation?.Dispose();
        _cameraCancellation = null;
        _captureLoop = null;
        lock (_captureSync)
        {
            _capture?.Dispose();
            _capture = null;
        }
        _activeCameraIndex = -1;
        _activeCameraDisplayName = string.Empty;
        _actualWidth = 0;
        _actualHeight = 0;
        _actualFramesPerSecond = 0;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static void CopyCameraOptions(CameraOptions source, CameraOptions destination)
    {
        destination.CameraIndex = source.CameraIndex;
        destination.SourceKind = source.SourceKind;
        destination.AutoSelectBestCamera = source.AutoSelectBestCamera;
        destination.ProbeCameraCount = source.ProbeCameraCount;
        destination.MinimumResolutionRatio = source.MinimumResolutionRatio;
        destination.Width = source.Width;
        destination.Height = source.Height;
        destination.FramesPerSecond = source.FramesPerSecond;
        destination.Codec = source.Codec;
        destination.Brightness = source.Brightness;
        destination.Contrast = source.Contrast;
        destination.Sharpness = source.Sharpness;
        destination.Saturation = source.Saturation;
        destination.AutoFocus = source.AutoFocus;
        destination.NetworkStreamUrl = source.NetworkStreamUrl;
        destination.NetworkUsername = source.NetworkUsername;
        destination.NetworkPasswordProtected = source.NetworkPasswordProtected;
        destination.HikvisionHost = source.HikvisionHost;
        destination.HikvisionRtspPort = source.HikvisionRtspPort;
        destination.HikvisionChannel = source.HikvisionChannel;
        destination.HikvisionSubStream = source.HikvisionSubStream;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(character => invalid.Contains(character) ? '_' : character).ToArray();
        return new string(chars);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
