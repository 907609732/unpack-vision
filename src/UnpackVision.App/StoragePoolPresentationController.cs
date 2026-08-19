using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using UnpackVision.Core;
using UnpackVision.Core.Recording;

namespace UnpackVision.App;

/// <summary>
/// Owns the editable storage-pool draft shown by WPF. The controller deliberately keeps
/// filesystem probing behind <see cref="IStoragePoolMonitor"/> so the window only handles
/// dialogs, selection and UI-thread lifetime.
/// </summary>
public sealed class StoragePoolPresentationController : INotifyPropertyChanged
{
    private readonly IStoragePoolMonitor _monitor;
    private readonly long _estimatedBytesPerHour;
    private readonly long _projectedMaximumRecordBytes;
    private readonly StoragePoolOptions _policySource;
    private string _summaryText = "正在读取录像盘位…";
    private bool _isRefreshing;

    public StoragePoolPresentationController(
        StoragePoolOptions options,
        IStoragePoolMonitor monitor,
        long estimatedBytesPerHour,
        long projectedMaximumRecordBytes)
    {
        ArgumentNullException.ThrowIfNull(options);
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _estimatedBytesPerHour = Math.Max(0, estimatedBytesPerHour);
        _projectedMaximumRecordBytes = Math.Max(0, projectedMaximumRecordBytes);
        _policySource = CloneOptions(options);

        var targets = (options.Targets ?? [])
            .OrderBy(target => target.Priority)
            .ThenBy(target => target.Id, StringComparer.OrdinalIgnoreCase)
            .Select(target => new StorageTargetRowViewModel(CloneTarget(target)));
        foreach (var target in targets)
        {
            target.PropertyChanged += Target_OnPropertyChanged;
            Targets.Add(target);
        }
        NormalizePriorities();
        UpdateSummary();
    }

    public ObservableCollection<StorageTargetRowViewModel> Targets { get; } = [];

    public string SummaryText
    {
        get => _summaryText;
        private set => SetField(ref _summaryText, value);
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set => SetField(ref _isRefreshing, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public StorageTargetRowViewModel AddTarget(string rootPath)
    {
        var normalizedPath = NormalizeRoot(rootPath);
        var existing = Targets.FirstOrDefault(target =>
            PathsEqual(target.RootPath, normalizedPath));
        if (existing is not null)
        {
            throw new ArgumentException("该录像目录已经在盘位列表中。", nameof(rootPath));
        }

        var row = new StorageTargetRowViewModel(new StorageTarget
        {
            Id = Guid.NewGuid().ToString("N"),
            DisplayName = CreateDefaultName(normalizedPath),
            RootPath = normalizedPath,
            Priority = Targets.Count,
            Enabled = true
        });
        row.PropertyChanged += Target_OnPropertyChanged;
        Targets.Add(row);
        NormalizePriorities();
        UpdateSummary();
        return row;
    }

    public bool RemoveTarget(StorageTargetRowViewModel? target)
    {
        if (target is null || !Targets.Remove(target))
        {
            return false;
        }
        target.PropertyChanged -= Target_OnPropertyChanged;
        NormalizePriorities();
        UpdateSummary();
        return true;
    }

    public bool MoveTarget(StorageTargetRowViewModel? target, int delta)
    {
        if (target is null || delta == 0)
        {
            return false;
        }
        var currentIndex = Targets.IndexOf(target);
        var nextIndex = currentIndex + delta;
        if (currentIndex < 0 || nextIndex < 0 || nextIndex >= Targets.Count)
        {
            return false;
        }
        Targets.Move(currentIndex, nextIndex);
        NormalizePriorities();
        UpdateSummary();
        return true;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;
        SummaryText = "正在检查所有录像盘位…";
        try
        {
            var options = BuildOptions(validate: false);
            var state = await _monitor.InspectAsync(
                options,
                new StorageCapacityRequest(
                    _projectedMaximumRecordBytes,
                    _estimatedBytesPerHour),
                cancellationToken);
            var stateById = state.Targets.ToDictionary(item => item.TargetId, StringComparer.OrdinalIgnoreCase);
            foreach (var target in Targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stateById.TryGetValue(target.Id, out var targetState))
                {
                    target.ApplyState(targetState);
                }
                else
                {
                    target.ApplyUnavailable("尚未取得盘位状态");
                }
            }
            UpdateSummary();
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    public StoragePoolOptions BuildOptions() => BuildOptions(validate: true);

    public string GetPrimaryRecordingRoot()
    {
        var target = Targets.FirstOrDefault(item => item.Enabled && !string.IsNullOrWhiteSpace(item.RootPath));
        return target is null ? string.Empty : NormalizeRoot(target.RootPath);
    }

    public void SetPrimaryRecordingRoot(string rootPath)
    {
        var normalized = NormalizeRoot(rootPath);
        var primary = Targets.FirstOrDefault(target => target.Enabled) ?? Targets.FirstOrDefault();
        if (primary is null)
        {
            AddTarget(normalized);
            return;
        }

        var duplicate = Targets.FirstOrDefault(target =>
            !ReferenceEquals(target, primary) && PathsEqual(target.RootPath, normalized));
        if (duplicate is not null)
        {
            throw new ArgumentException("该目录已经配置为其他录像盘位。", nameof(rootPath));
        }
        primary.Enabled = true;
        primary.RootPath = normalized;
        primary.VolumeId = string.Empty;
        primary.ClearState("保存后将重新检查此盘位");
        UpdateSummary();
    }

    public static long EstimateBytesPerHour(IEnumerable<CameraProfile> cameras, bool includeComposite)
    {
        ArgumentNullException.ThrowIfNull(cameras);
        var totalMegabitsPerSecond = cameras
            .Where(camera => camera.Enabled)
            .Sum(camera => Math.Max(
                2d,
                camera.Width * (double)camera.Height * camera.FramesPerSecond * 0.08d / 1_000_000d));
        if (includeComposite)
        {
            totalMegabitsPerSecond += 8d;
        }
        var bytesPerHour = totalMegabitsPerSecond * 1_000_000d / 8d * 3600d;
        return bytesPerHour >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(bytesPerHour);
    }

    private StoragePoolOptions BuildOptions(bool validate)
    {
        if (validate && Targets.Count == 0)
        {
            throw new ArgumentException("请至少添加一个录像盘位。", nameof(Targets));
        }

        var result = new StoragePoolOptions
        {
            WarningFreeSpacePercent = _policySource.WarningFreeSpacePercent,
            WarningRemainingRecordingTime = _policySource.WarningRemainingRecordingTime,
            MinimumSafeReserveBytes = _policySource.MinimumSafeReserveBytes,
            SafeReservePaddingBytes = _policySource.SafeReservePaddingBytes
        };
        var normalizedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < Targets.Count; index++)
        {
            var row = Targets[index];
            var displayName = row.DisplayName.Trim();
            if (validate && string.IsNullOrWhiteSpace(displayName))
            {
                throw new ArgumentException($"第 {index + 1} 个盘位名称不能为空。", nameof(Targets));
            }

            string rootPath;
            try
            {
                rootPath = NormalizeRoot(row.RootPath);
            }
            catch (ArgumentException) when (!validate)
            {
                rootPath = row.RootPath.Trim();
            }
            if (validate && !normalizedRoots.Add(rootPath))
            {
                throw new ArgumentException($"录像目录重复：{rootPath}", nameof(Targets));
            }

            result.Targets.Add(new StorageTarget
            {
                Id = string.IsNullOrWhiteSpace(row.Id) ? Guid.NewGuid().ToString("N") : row.Id,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? $"录像盘位 {index + 1}" : displayName,
                RootPath = rootPath,
                VolumeId = row.VolumeId,
                Priority = index,
                Enabled = row.Enabled
            });
        }

        if (validate && result.Targets.All(target => !target.Enabled))
        {
            throw new ArgumentException("请至少启用一个录像盘位。", nameof(Targets));
        }
        return result;
    }

    private void NormalizePriorities()
    {
        var primaryAssigned = false;
        for (var index = 0; index < Targets.Count; index++)
        {
            Targets[index].Priority = index;
            var isPrimary = !primaryAssigned && Targets[index].Enabled;
            Targets[index].SetPrimary(isPrimary);
            primaryAssigned |= isPrimary;
        }
    }

    private void UpdateSummary()
    {
        var enabled = Targets.Where(target => target.Enabled).ToArray();
        var primary = enabled.FirstOrDefault();
        var ready = enabled.Count(target => target.Health is StorageTargetHealth.Ready or StorageTargetHealth.Warning);
        SummaryText = primary is null
            ? $"共 {Targets.Count} 个盘位 · 尚未启用主盘"
            : $"共 {Targets.Count} 个盘位，启用 {enabled.Length} 个 · 当前主盘：{primary.DisplayName} · 可用 {ready}/{enabled.Length}";
    }

    private void Target_OnPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(StorageTargetRowViewModel.Enabled) or
            nameof(StorageTargetRowViewModel.DisplayName))
        {
            if (eventArgs.PropertyName == nameof(StorageTargetRowViewModel.Enabled))
            {
                NormalizePriorities();
            }
            UpdateSummary();
        }
    }

    private static string CreateDefaultName(string rootPath)
    {
        var root = Path.GetPathRoot(rootPath);
        if (!string.IsNullOrWhiteSpace(root))
        {
            return $"录像盘 {Path.TrimEndingDirectorySeparator(root)}";
        }
        return $"录像盘位";
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(NormalizeRoot(left), NormalizeRoot(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string NormalizeRoot(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("录像盘位目录不能为空。", nameof(value));
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value.Trim()));
    }

    private static StoragePoolOptions CloneOptions(StoragePoolOptions source) => new()
    {
        Targets = (source.Targets ?? []).Select(CloneTarget).ToList(),
        WarningFreeSpacePercent = source.WarningFreeSpacePercent,
        WarningRemainingRecordingTime = source.WarningRemainingRecordingTime,
        MinimumSafeReserveBytes = source.MinimumSafeReserveBytes,
        SafeReservePaddingBytes = source.SafeReservePaddingBytes
    };

    private static StorageTarget CloneTarget(StorageTarget source) => new()
    {
        Id = source.Id,
        DisplayName = source.DisplayName,
        RootPath = source.RootPath,
        VolumeId = source.VolumeId,
        Priority = source.Priority,
        Enabled = source.Enabled
    };

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed class StorageTargetRowViewModel : INotifyPropertyChanged
{
    private string _displayName;
    private string _rootPath;
    private string _volumeId;
    private int _priority;
    private bool _enabled;
    private bool _isPrimary;
    private StorageTargetHealth _health = StorageTargetHealth.Offline;
    private string _statusText = "尚未检查";
    private string _capacityText = "容量：--";
    private string _remainingText = "预计可录：--";
    private string _warningText = string.Empty;

    public StorageTargetRowViewModel(StorageTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Id = target.Id;
        _displayName = target.DisplayName;
        _rootPath = target.RootPath;
        _volumeId = target.VolumeId;
        _priority = target.Priority;
        _enabled = target.Enabled;
    }

    public string Id { get; }

    public string DisplayName
    {
        get => _displayName;
        set => SetField(ref _displayName, value ?? string.Empty);
    }

    public string RootPath
    {
        get => _rootPath;
        set => SetField(ref _rootPath, value ?? string.Empty);
    }

    public string VolumeId
    {
        get => _volumeId;
        set => SetField(ref _volumeId, value ?? string.Empty);
    }

    public int Priority
    {
        get => _priority;
        set
        {
            if (SetField(ref _priority, value))
            {
                OnPropertyChanged(nameof(PriorityText));
            }
        }
    }

    public string PriorityText => IsPrimary
        ? $"主盘 · 配置顺序 {Priority + 1}"
        : $"备用盘 · 配置顺序 {Priority + 1}";

    public bool IsPrimary
    {
        get => _isPrimary;
        private set
        {
            if (SetField(ref _isPrimary, value))
            {
                OnPropertyChanged(nameof(PriorityText));
            }
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetField(ref _enabled, value))
            {
                OnPropertyChanged(nameof(Opacity));
            }
        }
    }

    public double Opacity => Enabled ? 1 : 0.58;

    public StorageTargetHealth Health
    {
        get => _health;
        private set => SetField(ref _health, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string CapacityText
    {
        get => _capacityText;
        private set => SetField(ref _capacityText, value);
    }

    public string RemainingText
    {
        get => _remainingText;
        private set => SetField(ref _remainingText, value);
    }

    public string WarningText
    {
        get => _warningText;
        private set => SetField(ref _warningText, value);
    }

    public string StatusBrush => Health switch
    {
        StorageTargetHealth.Ready => "#16894B",
        StorageTargetHealth.Warning => "#B56A00",
        StorageTargetHealth.Disabled => "#7B8492",
        _ => "#C63C3C"
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void ApplyState(StorageTargetState state)
    {
        Health = state.Health;
        if (string.IsNullOrWhiteSpace(VolumeId) && !string.IsNullOrWhiteSpace(state.VolumeId))
        {
            VolumeId = state.VolumeId;
        }
        StatusText = TranslateHealth(state.Health);
        CapacityText = state.TotalBytes <= 0
            ? "容量：--"
            : $"剩余 {FormatBytes(state.EffectiveAvailableBytes)} / {FormatBytes(state.TotalBytes)}（{state.FreeSpacePercent:0.#}%）";
        RemainingText = state.EstimatedRecordingTime is null
            ? "预计可录：需先取得当前机位码率"
            : $"预计可录：{FormatDuration(state.EstimatedRecordingTime.Value)}";
        WarningText = CreateWarningText(state);
        OnPropertyChanged(nameof(StatusBrush));
    }

    internal void SetPrimary(bool value) => IsPrimary = value;

    internal void ApplyUnavailable(string message)
    {
        Health = Enabled ? StorageTargetHealth.Offline : StorageTargetHealth.Disabled;
        StatusText = Enabled ? "不可用" : "已停用";
        CapacityText = "容量：--";
        RemainingText = "预计可录：--";
        WarningText = message;
        OnPropertyChanged(nameof(StatusBrush));
    }

    internal void ClearState(string message)
    {
        ApplyUnavailable(message);
    }

    private static string TranslateHealth(StorageTargetHealth health) => health switch
    {
        StorageTargetHealth.Ready => "可用",
        StorageTargetHealth.Warning => "空间预警",
        StorageTargetHealth.Disabled => "已停用",
        StorageTargetHealth.Offline => "离线",
        StorageTargetHealth.ReadOnly => "只读",
        StorageTargetHealth.VolumeMismatch => "磁盘已更换",
        StorageTargetHealth.InsufficientSpace => "空间不足",
        _ => "未知"
    };

    private static string CreateWarningText(StorageTargetState state)
    {
        var warnings = new List<string>();
        if (state.WarningReasons.HasFlag(StorageWarningReason.LowFreeSpacePercentage))
        {
            warnings.Add("剩余空间低于 15%");
        }
        if (state.WarningReasons.HasFlag(StorageWarningReason.LowEstimatedRecordingTime))
        {
            warnings.Add("按当前码率预计不足 2 小时");
        }
        if (!string.IsNullOrWhiteSpace(state.Message))
        {
            warnings.Add(state.Message);
        }
        return string.Join(" · ", warnings);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration == TimeSpan.MaxValue)
        {
            return "很长";
        }
        if (duration.TotalHours >= 24)
        {
            return $"{duration.TotalDays:0.#} 天";
        }
        return duration.TotalHours >= 1
            ? $"{duration.TotalHours:0.#} 小时"
            : $"{Math.Max(0, duration.TotalMinutes):0} 分钟";
    }

    private static string FormatBytes(long bytes)
    {
        var value = Math.Max(0, bytes);
        if (value >= 1024L * 1024 * 1024 * 1024)
        {
            return $"{value / (1024d * 1024 * 1024 * 1024):0.##} TB";
        }
        if (value >= 1024L * 1024 * 1024)
        {
            return $"{value / (1024d * 1024 * 1024):0.##} GB";
        }
        return $"{value / (1024d * 1024):0.##} MB";
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
