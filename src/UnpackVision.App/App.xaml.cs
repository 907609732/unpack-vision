using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using UnpackVision.Core;
using UnpackVision.Infrastructure;
using UnpackVision.Infrastructure.Diagnostics;

namespace UnpackVision.App;

public partial class App : System.Windows.Application
{
    private const string InstanceMutexName = @"Local\UnpackVision.Desktop.SingleInstance";
    private Mutex? _instanceMutex;
    internal static UiHangWatchdog? UiWatchdog { get; private set; }
    private static readonly Lazy<DesktopUpdateService> UpdateService = new(() => new DesktopUpdateService());
    internal static DesktopUpdateService Updates => UpdateService.Value;
    private static readonly HttpClient TelemetryHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    protected override async void OnStartup(StartupEventArgs e)
    {
        UiWatchdog = new UiHangWatchdog(Dispatcher);
        DispatcherUnhandledException += (_, eventArgs) =>
            DiagnosticLog.Fatal(eventArgs.Exception, "WPF 界面线程发生未处理异常");
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
            DiagnosticLog.Information("开始加载桌面端设置");
            var settingsStore = new LocalSettingsStore();
            var hadSavedSettings = File.Exists(settingsStore.Path);
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
                settings.Consent.OptionalUsageTelemetryEnabled = consentWindow.TelemetryEnabled;
                settings.Telemetry.Enabled = consentWindow.TelemetryEnabled;
                settings.Telemetry.ChangedAt = DateTimeOffset.Now;
                settings.Telemetry.WithdrawnAt = consentWindow.TelemetryEnabled
                    ? null
                    : DateTimeOffset.Now;
                await settingsStore.SaveAsync(settings);
            }

            if (!settings.Setup.IsComplete &&
                hadSavedSettings &&
                Directory.Exists(settings.RecordingRoot) &&
                (string.IsNullOrWhiteSpace(settings.ExcelWorkbookPath) ||
                 File.Exists(settings.ExcelWorkbookPath)))
            {
                var manifest = await new PortableRecordCatalog(settings.RecordingRoot)
                    .EnsureWorkspaceAsync();
                settings.Setup.Version = SetupState.CurrentVersion;
                settings.Setup.WorkspaceId = manifest.WorkspaceId;
                settings.Setup.CompletedAt = DateTimeOffset.Now;
                settings.Setup.ExcelSkipped = string.IsNullOrWhiteSpace(settings.ExcelWorkbookPath);
                await settingsStore.SaveAsync(settings);
            }

            if (!settings.Setup.IsComplete ||
                !Directory.Exists(settings.RecordingRoot) ||
                (!string.IsNullOrWhiteSpace(settings.ExcelWorkbookPath) &&
                 !File.Exists(settings.ExcelWorkbookPath)))
            {
                var setupWindow = new SetupWizardWindow(settings);
                if (setupWindow.ShowDialog() != true || setupWindow.SavedSettings is null)
                {
                    Shutdown();
                    return;
                }
                settings = setupWindow.SavedSettings;
                await settingsStore.SaveAsync(settings);
            }

            StartupRegistration.EnsureCurrentUserStartup();
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            mainWindow.Show();
            DiagnosticLog.Information("桌面端主窗口已显示");
            _ = ApplyTelemetryAsync(settings);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error(exception, "桌面端启动流程失败");
            MessageBox.Show(
                exception.ToString(),
                "拆包智录启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    internal static async Task ApplyTelemetryAsync(
        LocalSettings settings,
        CancellationToken cancellationToken = default)
    {
        var telemetry = new CloudflareUsageTelemetry(
            TelemetryHttpClient,
            new TelemetryOptions
            {
                Enabled = settings.Telemetry.Enabled,
                Endpoint = ProductInfo.TelemetryEndpoint,
                Platform = "windows",
                AppVersion = ProductInfo.Version
            });
        if (settings.Telemetry.Enabled)
        {
            await telemetry.TrackAsync("app.daily_active", cancellationToken: cancellationToken);
        }
        else
        {
            await telemetry.DeleteLocalIdentityAsync(cancellationToken);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        UiWatchdog?.Dispose();
        UiWatchdog = null;
        if (_instanceMutex is not null)
        {
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
        }
        DiagnosticLog.Information("桌面端退出");
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
