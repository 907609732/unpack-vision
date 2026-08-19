using Velopack;
using UnpackVision.Infrastructure;
using UnpackVision.Infrastructure.Diagnostics;

namespace UnpackVision.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            .SetArgs(args)
            .SetAutoApplyOnStartup(false)
            .OnBeforeUninstallFastCallback(_ =>
            {
                try { StartupRegistration.RemoveCurrentUserStartup(); } catch { }
                try
                {
                    using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    StationHostConnection.StopAsync(shutdownTimeout.Token).GetAwaiter().GetResult();
                }
                catch { }
                UninstallCleanupService.ExecutePendingPlan();
            })
            .Run();

        DiagnosticLog.Initialize("desktop", ProductInfo.Version);
        DiagnosticLog.RegisterGlobalExceptionHandlers();
        try
        {
            DiagnosticLog.Information("桌面端开始启动");
            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
        catch (Exception exception)
        {
            DiagnosticLog.Fatal(exception, "桌面端发生致命启动错误");
            throw;
        }
        finally
        {
            DiagnosticLog.CloseAndFlush();
        }
    }
}
