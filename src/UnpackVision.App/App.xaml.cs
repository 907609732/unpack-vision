using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using UnpackVision.Infrastructure;

namespace UnpackVision.App;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\UnpackVision.Desktop.SingleInstance";
    private Mutex? _instanceMutex;
    internal static DesktopUpdateService Updates { get; } = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            ActivateExistingWindow();
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);
        try
        {
            var settingsStore = new LocalSettingsStore();
            var settings = await settingsStore.LoadAsync();
            if (!settings.Consent.IsCurrent(
                    LegalDocuments.TermsVersion,
                    LegalDocuments.PrivacyPolicyVersion))
            {
                var consentWindow = new FirstRunConsentWindow();
                if (consentWindow.ShowDialog() != true)
                {
                    Shutdown();
                    return;
                }
                settings.Consent.TermsVersion = LegalDocuments.TermsVersion;
                settings.Consent.PrivacyPolicyVersion = LegalDocuments.PrivacyPolicyVersion;
                settings.Consent.AcceptedAt = DateTimeOffset.Now;
                settings.Consent.OptionalUsageTelemetryEnabled = false;
                await settingsStore.SaveAsync(settings);
            }

            StartupRegistration.EnsureCurrentUserStartup();
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.ToString(),
                "电商拆包智能录像启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_instanceMutex is not null)
        {
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
        }
        base.OnExit(e);
    }

    private static void ActivateExistingWindow()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            using (process)
            {
                if (process.Id == current.Id || process.MainWindowHandle == IntPtr.Zero)
                {
                    continue;
                }
                ShowWindow(process.MainWindowHandle, 9);
                SetForegroundWindow(process.MainWindowHandle);
                return;
            }
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
