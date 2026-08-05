using Velopack;
using UnpackVision.Infrastructure.Diagnostics;

namespace UnpackVision.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        DiagnosticLog.Initialize("desktop", ProductInfo.Version);
        DiagnosticLog.RegisterGlobalExceptionHandlers();
        try
        {
            DiagnosticLog.Information("桌面端开始启动");
            VelopackApp.Build()
                .SetArgs(args)
                .SetAutoApplyOnStartup(false)
                .Run();

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
