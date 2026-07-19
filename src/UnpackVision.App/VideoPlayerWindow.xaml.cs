using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace UnpackVision.App;

public partial class VideoPlayerWindow : Window
{
    private readonly DispatcherTimer _timer;
    private bool _playing = true;
    private bool _seeking;

    public VideoPlayerWindow(string videoPath)
    {
        InitializeComponent();
        Title = Path.GetFileName(videoPath);
        TitleText.Text = Path.GetFileName(videoPath);
        Player.Source = new Uri(videoPath, UriKind.Absolute);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, Timer_OnTick, Dispatcher);
        Loaded += (_, _) =>
        {
            Player.Play();
            _timer.Start();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            Player.Stop();
        };
    }

    private void Player_OnMediaOpened(object sender, RoutedEventArgs e)
    {
        if (Player.NaturalDuration.HasTimeSpan)
        {
            ProgressSlider.Maximum = Player.NaturalDuration.TimeSpan.TotalSeconds;
        }
    }

    private void Player_OnMediaEnded(object sender, RoutedEventArgs e)
    {
        Player.Position = TimeSpan.Zero;
        Player.Pause();
        _playing = false;
        PlayPauseButton.Content = "播放";
    }

    private void Timer_OnTick(object? sender, EventArgs e)
    {
        if (!_seeking)
        {
            ProgressSlider.Value = Player.Position.TotalSeconds;
        }
        var total = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan : TimeSpan.Zero;
        TimeText.Text = $"{Format(Player.Position)} / {Format(total)}";
    }

    private void PlayPause_OnClick(object sender, RoutedEventArgs e)
    {
        if (_playing)
        {
            Player.Pause();
            PlayPauseButton.Content = "播放";
        }
        else
        {
            Player.Play();
            PlayPauseButton.Content = "暂停";
        }
        _playing = !_playing;
    }

    private void ProgressSlider_OnPreviewMouseDown(object sender, MouseButtonEventArgs e) => _seeking = true;

    private void ProgressSlider_OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        Player.Position = TimeSpan.FromSeconds(ProgressSlider.Value);
        _seeking = false;
    }

    private void Minimize_OnClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_OnClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
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
        }
    }

    private static string Format(TimeSpan value) => value.TotalHours >= 1
        ? value.ToString("hh\\:mm\\:ss")
        : value.ToString("mm\\:ss");
}
