using UnpackVision.Infrastructure.Diagnostics;

namespace UnpackVision.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DiagnosticLogCollection
{
    public const string Name = "DiagnosticLog";
}

[Collection(DiagnosticLogCollection.Name)]
public sealed class DiagnosticLogTests
{
    [Fact]
    public void DiagnosticLog_WritesFileAndRedactsSensitiveValues()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "UnpackVision.Tests",
            Guid.NewGuid().ToString("N"));
        const string token = "pairing-token-should-never-appear";
        const string trackingNo = "YT1234567890123456";
        const string videoPath = @"C:\Users\private\recordings\YT1234567890123456.mp4";

        try
        {
            DiagnosticLog.Initialize("diagnostic-test", "test", root);
            DiagnosticLog.Information(
                "测试敏感属性 {Token} {TrackingNo} {VideoPath}",
                token,
                trackingNo,
                videoPath);
            DiagnosticLog.Error(
                new IOException($@"无法打开 ""{videoPath}""?token={token}; trackingNo={trackingNo}"),
                "测试异常输出");
            DiagnosticLog.CloseAndFlush();

            var logFile = Assert.Single(
                Directory.GetFiles(root, "*.log", SearchOption.AllDirectories));
            var content = File.ReadAllText(logFile);

            Assert.Contains("诊断日志已启动", content, StringComparison.Ordinal);
            Assert.Contains("测试异常输出", content, StringComparison.Ordinal);
            Assert.DoesNotContain(token, content, StringComparison.Ordinal);
            Assert.DoesNotContain(trackingNo, content, StringComparison.Ordinal);
            Assert.DoesNotContain(videoPath, content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("[REDACTED]", content, StringComparison.Ordinal);
            Assert.Contains("[PATH]", content, StringComparison.Ordinal);
        }
        finally
        {
            DiagnosticLog.CloseAndFlush();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
