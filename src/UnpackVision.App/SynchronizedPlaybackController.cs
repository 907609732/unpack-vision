using System.IO;
using UnpackVision.Core;

namespace UnpackVision.App;

/// <summary>
/// Describes how many decoders the current machine is allowed to run together.
/// The default remains deliberately conservative; a later capability probe can
/// supply an eight- or sixteen-stream instance without changing timeline logic.
/// </summary>
public sealed record SynchronizedPlaybackCapabilities
{
    public const int ProductionMaximumStreams = 16;

    public SynchronizedPlaybackCapabilities(int maximumSimultaneousStreams = 4)
    {
        if (maximumSimultaneousStreams is < 1 or > ProductionMaximumStreams)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumSimultaneousStreams),
                $"同步回放路数必须在 1 到 {ProductionMaximumStreams} 之间。");
        }

        MaximumSimultaneousStreams = maximumSimultaneousStreams;
    }

    public int MaximumSimultaneousStreams { get; }

    public IReadOnlyList<double> SupportedSpeeds { get; } = [0.5, 1, 1.5, 2];

    public static SynchronizedPlaybackCapabilities Default { get; } = new();
}

public enum PlaybackPlaceholderKind
{
    None,
    Loading,
    MissingFile,
    WaitingForStart,
    StreamEnded,
    MediaFailed
}

public sealed record SynchronizedPlaybackAsset(
    RecordMediaAsset Asset,
    bool IsSelected,
    bool IsFileAvailable,
    string AvailabilityText)
{
    public Guid Id => Asset.Id;
    public string DisplayName => string.IsNullOrWhiteSpace(Asset.DisplayName) ? "未命名机位" : Asset.DisplayName;
    public MediaIntegrityStatus Integrity => Asset.Integrity;
}

public sealed record PlaybackDirective(
    TimeSpan DesiredLocalPosition,
    bool ShouldRender,
    bool ShouldPlay,
    bool RequiresSeek,
    PlaybackPlaceholderKind Placeholder,
    string Message,
    long SynchronizationVersion);

/// <summary>
/// Owns the shared record timeline and computes per-file playback positions.
/// It intentionally has no dependency on WPF media controls so alignment and
/// future decoder-capability gates can be tested without opening real videos.
/// </summary>
public sealed class SynchronizedPlaybackController
{
    private static readonly TimeSpan DriftTolerance = TimeSpan.FromMilliseconds(250);
    private readonly List<AssetState> _assets;
    private readonly TimeSpan _recordDuration;
    private long _synchronizationVersion = 1;

    public SynchronizedPlaybackController(
        ScanRecord record,
        SynchronizedPlaybackCapabilities? capabilities = null,
        Func<string, bool>? fileExists = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        Capabilities = capabilities ?? SynchronizedPlaybackCapabilities.Default;
        fileExists ??= File.Exists;

        _recordDuration = CalculateRecordDuration(record);
        _assets = BuildAssets(record, fileExists)
            .Select(asset => new AssetState(asset, fileExists(asset.VideoPath)))
            .ToList();

        foreach (var asset in OrderForInitialSelection(record, _assets)
                     .Take(Capabilities.MaximumSimultaneousStreams))
        {
            asset.IsSelected = true;
        }
    }

    public SynchronizedPlaybackCapabilities Capabilities { get; }

    public IReadOnlyList<SynchronizedPlaybackAsset> Assets => _assets
        .Select(ToView)
        .ToArray();

    public IReadOnlyList<SynchronizedPlaybackAsset> SelectedAssets => _assets
        .Where(asset => asset.IsSelected)
        .Select(ToView)
        .ToArray();

    public TimeSpan Position { get; private set; }

    public TimeSpan Duration
    {
        get
        {
            var mediaDuration = _assets
                .Where(asset => asset.Duration is not null)
                .Select(asset => asset.Asset.StartOffset + asset.Duration!.Value)
                .DefaultIfEmpty(TimeSpan.Zero)
                .Max();
            return mediaDuration > _recordDuration ? mediaDuration : _recordDuration;
        }
    }

    public bool IsPlaying { get; private set; } = true;

    public double Speed { get; private set; } = 1;

    public long SynchronizationVersion => _synchronizationVersion;

    public Guid? AudioAssetId => _assets
        .FirstOrDefault(asset => asset.IsSelected && asset.IsFileAvailable && asset.Asset.Role == RecordMediaRole.Primary)
        ?.Asset.Id;

    public bool TrySetSelected(Guid assetId, bool selected, out string message)
    {
        var asset = _assets.FirstOrDefault(candidate => candidate.Asset.Id == assetId);
        if (asset is null)
        {
            message = "未找到这个机位的录像。";
            return false;
        }

        if (asset.IsSelected == selected)
        {
            message = string.Empty;
            return true;
        }

        if (selected && _assets.Count(candidate => candidate.IsSelected) >= Capabilities.MaximumSimultaneousStreams)
        {
            message = $"当前电脑最多同时回放 {Capabilities.MaximumSimultaneousStreams} 路录像。";
            return false;
        }

        if (!selected && _assets.Count(candidate => candidate.IsSelected) == 1)
        {
            message = "至少保留一个回放画面。";
            return false;
        }

        asset.IsSelected = selected;
        RequestSynchronization();
        message = string.Empty;
        return true;
    }

    public void SetMediaDuration(Guid assetId, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        var asset = _assets.FirstOrDefault(candidate => candidate.Asset.Id == assetId);
        if (asset is not null)
        {
            asset.Duration = duration;
        }
    }

    public void MarkMediaFailed(Guid assetId, string? reason)
    {
        var asset = _assets.FirstOrDefault(candidate => candidate.Asset.Id == assetId);
        if (asset is null)
        {
            return;
        }

        asset.MediaFailure = string.IsNullOrWhiteSpace(reason) ? "媒体文件无法打开。" : reason.Trim();
    }

    public void Play()
    {
        if (Duration > TimeSpan.Zero && Position >= Duration)
        {
            Seek(TimeSpan.Zero);
        }

        IsPlaying = true;
    }

    public void Pause() => IsPlaying = false;

    public void TogglePlayback()
    {
        if (IsPlaying)
        {
            Pause();
        }
        else
        {
            Play();
        }
    }

    public void SetSpeed(double speed)
    {
        if (!Capabilities.SupportedSpeeds.Contains(speed))
        {
            throw new ArgumentOutOfRangeException(nameof(speed), "不支持这个播放速度。");
        }

        Speed = speed;
        RequestSynchronization();
    }

    public void Seek(TimeSpan position)
    {
        var duration = Duration;
        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }
        else if (duration > TimeSpan.Zero && position > duration)
        {
            position = duration;
        }

        Position = position;
        RequestSynchronization();
    }

    public void Advance(TimeSpan elapsed)
    {
        if (!IsPlaying || elapsed <= TimeSpan.Zero)
        {
            return;
        }

        var scaledTicks = (long)Math.Round(elapsed.Ticks * Speed, MidpointRounding.AwayFromZero);
        Position += TimeSpan.FromTicks(scaledTicks);
        var duration = Duration;
        if (duration > TimeSpan.Zero && Position >= duration)
        {
            Position = duration;
            IsPlaying = false;
            RequestSynchronization();
        }
    }

    public PlaybackDirective GetDirective(
        Guid assetId,
        TimeSpan currentLocalPosition,
        bool mediaReady,
        long appliedSynchronizationVersion)
    {
        var state = _assets.FirstOrDefault(candidate => candidate.Asset.Id == assetId)
            ?? throw new ArgumentException("未找到这个机位的录像。", nameof(assetId));

        if (!state.IsFileAvailable)
        {
            return Placeholder(state, PlaybackPlaceholderKind.MissingFile, "录像文件不存在。", appliedSynchronizationVersion);
        }

        if (!string.IsNullOrWhiteSpace(state.MediaFailure))
        {
            return Placeholder(state, PlaybackPlaceholderKind.MediaFailed, state.MediaFailure, appliedSynchronizationVersion);
        }

        var localPosition = Position - state.Asset.StartOffset;
        if (localPosition < TimeSpan.Zero)
        {
            return Placeholder(
                state,
                PlaybackPlaceholderKind.WaitingForStart,
                $"等待机位开始 · +{FormatOffset(state.Asset.StartOffset)}",
                appliedSynchronizationVersion);
        }

        if (state.Duration is { } mediaDuration && localPosition >= mediaDuration)
        {
            return Placeholder(state, PlaybackPlaceholderKind.StreamEnded, "该机位录像已结束。", appliedSynchronizationVersion);
        }

        var versionChanged = appliedSynchronizationVersion != _synchronizationVersion;
        var drift = (currentLocalPosition - localPosition).Duration();
        return new PlaybackDirective(
            localPosition,
            ShouldRender: mediaReady,
            ShouldPlay: mediaReady && IsPlaying,
            RequiresSeek: mediaReady && (versionChanged || drift > DriftTolerance),
            Placeholder: mediaReady ? PlaybackPlaceholderKind.None : PlaybackPlaceholderKind.Loading,
            Message: mediaReady ? string.Empty : "正在加载录像…",
            SynchronizationVersion: _synchronizationVersion);
    }

    private void RequestSynchronization() => _synchronizationVersion++;

    private PlaybackDirective Placeholder(
        AssetState state,
        PlaybackPlaceholderKind kind,
        string message,
        long appliedSynchronizationVersion)
    {
        var desired = Position <= state.Asset.StartOffset
            ? TimeSpan.Zero
            : Position - state.Asset.StartOffset;
        if (state.Duration is { } duration && desired > duration)
        {
            desired = duration;
        }

        return new PlaybackDirective(
            desired,
            ShouldRender: false,
            ShouldPlay: false,
            RequiresSeek: appliedSynchronizationVersion != _synchronizationVersion,
            Placeholder: kind,
            Message: message,
            SynchronizationVersion: _synchronizationVersion);
    }

    private static SynchronizedPlaybackAsset ToView(AssetState state) => new(
        state.Asset,
        state.IsSelected,
        state.IsFileAvailable,
        state.IsFileAvailable ? "可播放" : "文件缺失");

    private static IReadOnlyList<RecordMediaAsset> BuildAssets(ScanRecord record, Func<string, bool> fileExists)
    {
        if (record.MediaAssets.Count > 0)
        {
            return record.MediaAssets
                .GroupBy(asset => asset.Id)
                .Select(group => group.First())
                .ToArray();
        }

        var legacyPath = record.VideoPath ?? string.Empty;
        return
        [
            new RecordMediaAsset
            {
                RecordId = record.Id,
                CameraId = record.CameraId ?? "legacy-primary",
                DisplayName = "主机位",
                Role = RecordMediaRole.Primary,
                VideoPath = legacyPath,
                Integrity = string.IsNullOrWhiteSpace(legacyPath) || !fileExists(legacyPath)
                    ? MediaIntegrityStatus.Failed
                    : MediaIntegrityStatus.Complete,
                CreatedAt = record.CreatedAt,
                UpdatedAt = record.UpdatedAt
            }
        ];
    }

    private static IEnumerable<AssetState> OrderForInitialSelection(ScanRecord record, IReadOnlyList<AssetState> assets) =>
        assets
            .OrderBy(asset => asset.Asset.Id == record.DefaultMediaAssetId ? 0 : 1)
            .ThenBy(asset => asset.Asset.Role == RecordMediaRole.Primary ? 0 : 1)
            .ThenBy(asset => asset.Asset.Role == RecordMediaRole.Composite ? 0 : 1)
            .ThenBy(asset => asset.Asset.CreatedAt)
            .ThenBy(asset => asset.Asset.DisplayName, StringComparer.CurrentCulture);

    private static TimeSpan CalculateRecordDuration(ScanRecord record)
    {
        if (record.RecordingStartedAt is null || record.RecordingEndedAt is null)
        {
            return TimeSpan.Zero;
        }

        var duration = record.RecordingEndedAt.Value - record.RecordingStartedAt.Value;
        return duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
    }

    private static string FormatOffset(TimeSpan value) => value.TotalHours >= 1
        ? value.ToString("hh\\:mm\\:ss")
        : value.ToString("mm\\:ss");

    private sealed class AssetState(RecordMediaAsset asset, bool isFileAvailable)
    {
        public RecordMediaAsset Asset { get; } = asset;
        public bool IsFileAvailable { get; } = isFileAvailable;
        public bool IsSelected { get; set; }
        public TimeSpan? Duration { get; set; }
        public string? MediaFailure { get; set; }
    }
}
