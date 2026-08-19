using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace UnpackVision.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfUiCollection : ICollectionFixture<WpfTestHost>
{
    public const string Name = "WpfUi";
}

public sealed class WpfTestHost : IDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(1);

    private readonly ManualResetEventSlim _ready = new(initialState: false);
    private readonly Thread _thread;
    private Dispatcher? _dispatcher;
    private System.Windows.Application? _application;
    private Exception? _startupFailure;
    private bool _disposed;

    public WpfTestHost()
    {
        _thread = new Thread(RunDispatcher)
        {
            IsBackground = true,
            Name = "UnpackVision.WpfTestHost"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_ready.Wait(StartupTimeout))
        {
            throw new TimeoutException("WPF 测试宿主未在限定时间内启动。");
        }

        if (_startupFailure is not null)
        {
            throw new InvalidOperationException("WPF 测试宿主启动失败。", _startupFailure);
        }
    }

    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        GetDispatcher().Invoke(action, DispatcherPriority.Send);
    }

    public T Invoke<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return GetDispatcher().Invoke(action, DispatcherPriority.Send);
    }

    public void WaitUntil(Func<bool> condition, TimeSpan timeout, string timeoutMessage)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (Invoke(condition))
            {
                return;
            }

            Thread.Sleep(25);
        }

        throw new TimeoutException(timeoutMessage);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var dispatcher = _dispatcher;
        if (dispatcher is not null && !dispatcher.HasShutdownStarted)
        {
            try
            {
                _ = dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
                {
                    foreach (Window window in _application?.Windows.Cast<Window>().ToArray() ?? [])
                    {
                        window.Close();
                    }

                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                });
            }
            catch (InvalidOperationException) when (dispatcher.HasShutdownStarted)
            {
                // A test can close the last window while fixture disposal is being scheduled.
            }
        }

        // Some WPF controls keep an internal nested dispatcher frame alive after
        // selection automation. This is a background STA shared by the complete
        // collection, so the testhost process owns final reclamation after the
        // shutdown request; a passed UI assertion must not become a cleanup failure.
        _ = _thread.Join(ShutdownTimeout);

        _ready.Dispose();
    }

    private void RunDispatcher()
    {
        try
        {
            _application = new System.Windows.Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            AddWindowTestResources(_application.Resources);
            _dispatcher = Dispatcher.CurrentDispatcher;
            _ready.Set();
            Dispatcher.Run();
        }
        catch (Exception exception)
        {
            _startupFailure = exception;
            _ready.Set();
        }
    }

    private Dispatcher GetDispatcher()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_startupFailure is not null)
        {
            throw new InvalidOperationException("WPF 测试宿主已失败。", _startupFailure);
        }

        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            throw new InvalidOperationException("WPF 测试宿主当前不可用。");
        }

        return dispatcher;
    }

    private static void AddWindowTestResources(ResourceDictionary resources)
    {
        resources["BorderBrush"] = new SolidColorBrush(Color.FromRgb(221, 225, 231));

        var selectableText = new Style(typeof(TextBox));
        selectableText.Setters.Add(new Setter(TextBox.IsReadOnlyProperty, true));
        selectableText.Setters.Add(new Setter(TextBox.IsReadOnlyCaretVisibleProperty, true));
        selectableText.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        selectableText.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        selectableText.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        resources["SelectableText"] = selectableText;
    }
}
