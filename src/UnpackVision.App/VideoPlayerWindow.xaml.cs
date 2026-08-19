using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UnpackVision.Core;

namespace UnpackVision.App;

public partial class VideoPlayerWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _playbackClock = new();
    private readonly Dictionary<Guid, PlayerSurface> _surfaces = [];
    private readonly ScanRecord _record;
    private readonly SynchronizedPlaybackController _controller;
    private bool _seeking;
    private bool _refreshingAssetSelection;

    public VideoPlayerWindow(ScanRecord record)
    {
        _record = record ?? throw new ArgumentNullException(nameof(record));
        _controller = new SynchronizedPlaybackController(record);
        InitializeComponent();

        Title = $"{record.TrackingNo} · 同步回放";
        TitleText.Text = Title;
        IssueTimelineControl.ItemsSource = record.Tags
            .Where(tag => tag.IsActive)
            .OrderBy(tag => tag.TaggedAt)
            .ToArray();
        RefreshAssetSelector();

        _timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(100),
            DispatcherPriority.Background,
            Timer_OnTick,
            Dispatcher);
        Loaded += Window_OnLoaded;
        Closed += Window_OnClosed;
    }

    private void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        RebuildPlaybackSurfaces();
        _playbackClock.Restart();
        _timer.Start();
    }

    private void Window_OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        _playbackClock.Stop();
        DisposePlaybackSurfaces();
        Loaded -= Window_OnLoaded;
        Closed -= Window_OnClosed;
    }

    private void CameraAngle_OnChecked(object sender, RoutedEventArgs e) => ChangeAssetSelection(sender, selected: true);

    private void CameraAngle_OnUnchecked(object sender, RoutedEventArgs e) => ChangeAssetSelection(sender, selected: false);

    private void ChangeAssetSelection(object sender, bool selected)
    {
        if (_refreshingAssetSelection || (sender as FrameworkElement)?.Tag is not Guid assetId)
        {
            return;
        }

        var current = _controller.Assets.FirstOrDefault(asset => asset.Id == assetId);
        if (current is null || current.IsSelected == selected)
        {
            return;
        }

        if (!_controller.TrySetSelected(assetId, selected, out var message))
        {
            PlaybackStatusText.Text = message;
            RefreshAssetSelector();
            return;
        }

        RefreshAssetSelector();
        RebuildPlaybackSurfaces();
    }

    private void RefreshAssetSelector()
    {
        _refreshingAssetSelection = true;
        try
        {
            CameraAnglesControl.ItemsSource = null;
            CameraAnglesControl.ItemsSource = _controller.Assets;
            UpdatePlaybackSummary();
        }
        finally
        {
            _refreshingAssetSelection = false;
        }
    }

    private void RebuildPlaybackSurfaces()
    {
        DisposePlaybackSurfaces();
        PlaybackGrid.Children.Clear();
        PlaybackGrid.RowDefinitions.Clear();
        PlaybackGrid.ColumnDefinitions.Clear();

        var selectedAssets = _controller.SelectedAssets;
        var columns = selectedAssets.Count == 1 ? 1 : 2;
        var rows = selectedAssets.Count <= 2 ? 1 : 2;
        for (var row = 0; row < rows; row++)
        {
            PlaybackGrid.RowDefinitions.Add(new RowDefinition());
        }
        for (var column = 0; column < columns; column++)
        {
            PlaybackGrid.ColumnDefinitions.Add(new ColumnDefinition());
        }

        for (var index = 0; index < selectedAssets.Count; index++)
        {
            var asset = selectedAssets[index];
            var surface = CreatePlaybackSurface(asset);
            _surfaces.Add(asset.Id, surface);
            Grid.SetRow(surface.Root, index / columns);
            Grid.SetColumn(surface.Root, index % columns);
            PlaybackGrid.Children.Add(surface.Root);
        }

        UpdatePlaybackSummary();
        ApplyPlaybackDirectives();
    }

    private PlayerSurface CreatePlaybackSurface(SynchronizedPlaybackAsset asset)
    {
        var root = new Border
        {
            Margin = new Thickness(3),
            Background = new SolidColorBrush(Color.FromRgb(4, 6, 9)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(41, 48, 61)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true
        };
        var panel = new Grid();
        root.Child = panel;

        MediaElement? player = null;
        if (asset.IsFileAvailable)
        {
            player = new MediaElement
            {
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Stop,
                Stretch = Stretch.Uniform,
                ScrubbingEnabled = true,
                Volume = _controller.AudioAssetId == asset.Id ? 1 : 0,
                SpeedRatio = _controller.Speed,
                Tag = asset.Id
            };
            player.MediaOpened += Player_OnMediaOpened;
            player.MediaEnded += Player_OnMediaEnded;
            player.MediaFailed += Player_OnMediaFailed;
            try
            {
                player.Source = new Uri(Path.GetFullPath(asset.Asset.VideoPath), UriKind.Absolute);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                _controller.MarkMediaFailed(asset.Id, "录像路径无效。");
            }
            panel.Children.Add(player);
        }

        var placeholderText = new TextBlock
        {
            Text = asset.IsFileAvailable ? "正在加载录像…" : "录像文件不存在。",
            Foreground = new SolidColorBrush(Color.FromRgb(174, 184, 198)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            FontSize = 14,
            Margin = new Thickness(24)
        };
        var placeholder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(12, 16, 22)),
            Child = placeholderText
        };
        panel.Children.Add(placeholder);

        var label = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(10),
            Padding = new Thickness(9, 5, 9, 5),
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(Color.FromArgb(210, 20, 23, 30)),
            Child = new TextBlock
            {
                Text = BuildAssetLabel(asset),
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12
            }
        };
        panel.Children.Add(label);

        return new PlayerSurface(asset, root, player, placeholder, placeholderText);
    }

    private void Player_OnMediaOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MediaElement player || player.Tag is not Guid assetId || !_surfaces.TryGetValue(assetId, out var surface))
        {
            return;
        }

        surface.IsReady = true;
        if (player.NaturalDuration.HasTimeSpan)
        {
            _controller.SetMediaDuration(assetId, player.NaturalDuration.TimeSpan);
        }
        surface.AppliedSynchronizationVersion = 0;
        ApplyPlaybackDirectives();
        UpdateTimelineControls();
    }

    private void Player_OnMediaEnded(object sender, RoutedEventArgs e)
    {
        if (sender is MediaElement player && player.Tag is Guid assetId && _surfaces.TryGetValue(assetId, out var surface))
        {
            surface.IsPlaying = false;
        }
    }

    private void Player_OnMediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        if (sender is not MediaElement player || player.Tag is not Guid assetId || !_surfaces.TryGetValue(assetId, out var surface))
        {
            return;
        }

        surface.IsReady = false;
        surface.IsPlaying = false;
        _controller.MarkMediaFailed(assetId, "录像无法解码或文件已损坏。");
        ApplyPlaybackDirectives();
    }

    private void Timer_OnTick(object? sender, EventArgs e)
    {
        var elapsed = _playbackClock.Elapsed;
        _playbackClock.Restart();
        _controller.Advance(elapsed);
        ApplyPlaybackDirectives();
        UpdateTimelineControls();
    }

    private void ApplyPlaybackDirectives()
    {
        foreach (var surface in _surfaces.Values)
        {
            var currentPosition = surface.Player?.Position ?? TimeSpan.Zero;
            var directive = _controller.GetDirective(
                surface.Asset.Id,
                currentPosition,
                surface.IsReady,
                surface.AppliedSynchronizationVersion);

            surface.PlaceholderText.Text = directive.Message;
            surface.Placeholder.Visibility = directive.Placeholder == PlaybackPlaceholderKind.None
                ? Visibility.Collapsed
                : Visibility.Visible;
            if (surface.Player is null)
            {
                surface.AppliedSynchronizationVersion = directive.SynchronizationVersion;
                continue;
            }

            surface.Player.Visibility = directive.ShouldRender ? Visibility.Visible : Visibility.Hidden;
            surface.Player.SpeedRatio = _controller.Speed;
            if (directive.RequiresSeek && surface.IsReady)
            {
                surface.Player.Position = directive.DesiredLocalPosition;
            }
            surface.AppliedSynchronizationVersion = directive.SynchronizationVersion;

            if (directive.ShouldPlay && !surface.IsPlaying)
            {
                surface.Player.Play();
                surface.IsPlaying = true;
            }
            else if (!directive.ShouldPlay && surface.IsPlaying)
            {
                surface.Player.Pause();
                surface.IsPlaying = false;
            }
        }
    }

    private void UpdateTimelineControls()
    {
        var duration = _controller.Duration;
        if (!_seeking)
        {
            ProgressSlider.Maximum = Math.Max(1, duration.TotalSeconds);
            ProgressSlider.Value = Math.Min(ProgressSlider.Maximum, _controller.Position.TotalSeconds);
        }
        TimeText.Text = $"{Format(_controller.Position)} / {Format(duration)}";
        PlayPauseButton.Content = _controller.IsPlaying ? "暂停" : "播放";
    }

    private void UpdatePlaybackSummary()
    {
        var selected = _controller.SelectedAssets;
        var missingCount = selected.Count(asset => !asset.IsFileAvailable);
        SelectionSummaryText.Text = $"已选 {selected.Count}/{_controller.Capabilities.MaximumSimultaneousStreams} 路 · 主机位音频";
        PlaybackStatusText.Text = missingCount == 0
            ? "统一时间轴"
            : $"{missingCount} 路文件缺失";
    }

    private void PlayPause_OnClick(object sender, RoutedEventArgs e)
    {
        _controller.TogglePlayback();
        _playbackClock.Restart();
        ApplyPlaybackDirectives();
        UpdateTimelineControls();
    }

    private void ProgressSlider_OnPreviewMouseDown(object sender, MouseButtonEventArgs e) => _seeking = true;

    private void ProgressSlider_OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _controller.Seek(TimeSpan.FromSeconds(ProgressSlider.Value));
        _seeking = false;
        _playbackClock.Restart();
        ApplyPlaybackDirectives();
        UpdateTimelineControls();
    }

    private void SpeedComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeedComboBox?.SelectedItem is not ComboBoxItem item ||
            !double.TryParse(item.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
        {
            return;
        }

        _controller.SetSpeed(speed);
        _playbackClock.Restart();
        ApplyPlaybackDirectives();
    }

    private void IssueMarker_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RecordTagAssignment assignment || _record.RecordingStartedAt is null)
        {
            return;
        }

        _controller.Seek(assignment.TaggedAt - _record.RecordingStartedAt.Value);
        _controller.Play();
        _playbackClock.Restart();
        ApplyPlaybackDirectives();
        UpdateTimelineControls();
    }

    private void DisposePlaybackSurfaces()
    {
        foreach (var surface in _surfaces.Values)
        {
            if (surface.Player is null)
            {
                continue;
            }

            surface.Player.MediaOpened -= Player_OnMediaOpened;
            surface.Player.MediaEnded -= Player_OnMediaEnded;
            surface.Player.MediaFailed -= Player_OnMediaFailed;
            surface.Player.Stop();
            surface.Player.Source = null;
        }
        _surfaces.Clear();
    }

    private void Minimize_OnClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_OnClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();

    private void Window_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
        else if (e.Key == Key.Space)
        {
            PlayPause_OnClick(sender, e);
            e.Handled = true;
        }
    }

    private static string BuildAssetLabel(SynchronizedPlaybackAsset asset)
    {
        var role = asset.Asset.Role switch
        {
            RecordMediaRole.Primary => "主机位",
            RecordMediaRole.Composite => "合成",
            _ => "副机位"
        };
        return $"{asset.DisplayName} · {role}";
    }

    private static string Format(TimeSpan value) => value.TotalHours >= 1
        ? value.ToString("hh\\:mm\\:ss")
        : value.ToString("mm\\:ss");

    private sealed class PlayerSurface(
        SynchronizedPlaybackAsset asset,
        Border root,
        MediaElement? player,
        Border placeholder,
        TextBlock placeholderText)
    {
        public SynchronizedPlaybackAsset Asset { get; } = asset;
        public Border Root { get; } = root;
        public MediaElement? Player { get; } = player;
        public Border Placeholder { get; } = placeholder;
        public TextBlock PlaceholderText { get; } = placeholderText;
        public bool IsReady { get; set; }
        public bool IsPlaying { get; set; }
        public long AppliedSynchronizationVersion { get; set; }
    }
}
