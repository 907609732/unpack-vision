using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Enrichers.Sensitive;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting;
using Serilog.Formatting.Display;

namespace UnpackVision.Infrastructure.Diagnostics;

/// <summary>
/// Configures privacy-aware, process-local diagnostic logs for the executable hosts.
/// Business identifiers and credentials must still never be supplied to log calls.
/// </summary>
public static class DiagnosticLog
{
    private const long FileSizeLimitBytes = 20 * 1024 * 1024;
    private static readonly object Sync = new();
    private static int _globalHandlersRegistered;

    public static string LogRootDirectory { get; private set; } = GetDefaultRoot();

    public static string Initialize(
        string component,
        string version,
        string? rootDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        lock (Sync)
        {
            Log.CloseAndFlush();

            var safeComponent = string.Concat(component.Select(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                    ? character
                    : '-'));
            var root = Path.GetFullPath(
                rootDirectory ??
                Environment.GetEnvironmentVariable("UNPACKVISION_LOG_ROOT") ??
                GetDefaultRoot());
            var componentDirectory = Path.Combine(root, safeComponent);
            Directory.CreateDirectory(componentDirectory);

            var formatter = new RedactingTextFormatter(
                "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] " +
                "[{Component}] [{SourceContext}] {Properties:j} {Message:lj}" +
                "{NewLine}{Exception}");

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Component", safeComponent)
                .Enrich.WithProperty("Version", version)
                .Enrich.WithProperty("ProcessId", Environment.ProcessId)
                .Enrich.WithSensitiveDataMasking(options =>
                {
                    options.MaskValue = "[REDACTED]";
                    foreach (var property in SensitivePropertyNames)
                    {
                        options.MaskProperties.Add(new MaskProperty { Name = property });
                    }
                })
                .WriteTo.File(
                    formatter,
                    Path.Combine(componentDirectory, $"{safeComponent}-.log"),
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: FileSizeLimitBytes,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: 30,
                    retainedFileTimeLimit: TimeSpan.FromDays(14),
                    shared: true,
                    buffered: false)
                .CreateLogger();

            LogRootDirectory = root;
            Log.Information("诊断日志已启动，组件 {HostComponent}，版本 {HostVersion}", safeComponent, version);
            return componentDirectory;
        }
    }

    public static ILoggerProvider CreateLoggerProvider() =>
        new SerilogLoggerProvider(Log.Logger, dispose: false);

    public static void RegisterGlobalExceptionHandlers()
    {
        if (Interlocked.Exchange(ref _globalHandlersRegistered, 1) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
            {
                Log.Fatal(exception, "进程发生未处理异常，正在终止：{IsTerminating}", eventArgs.IsTerminating);
            }
            else
            {
                Log.Fatal("进程发生非 Exception 类型的未处理错误，正在终止：{IsTerminating}", eventArgs.IsTerminating);
            }
            Log.CloseAndFlush();
        };

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            Log.Error(eventArgs.Exception, "后台任务异常未被观察");
            eventArgs.SetObserved();
        };
    }

    public static void Information(string messageTemplate, params object?[] values) =>
        Log.Information(messageTemplate, values);

    public static void Warning(string messageTemplate, params object?[] values) =>
        Log.Warning(messageTemplate, values);

    public static void Warning(Exception exception, string messageTemplate, params object?[] values) =>
        Log.Warning(exception, messageTemplate, values);

    public static void Error(Exception exception, string messageTemplate, params object?[] values) =>
        Log.Error(exception, messageTemplate, values);

    public static void Fatal(Exception exception, string messageTemplate, params object?[] values) =>
        Log.Fatal(exception, messageTemplate, values);

    public static void CloseAndFlush()
    {
        lock (Sync)
        {
            Log.Information("诊断日志正在关闭");
            Log.CloseAndFlush();
        }
    }

    private static string GetDefaultRoot() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UnpackVision",
            "Logs");

    private static readonly string[] SensitivePropertyNames =
    [
        "ApiKey",
        "Authorization",
        "CertificateFingerprint",
        "DeviceId",
        "ExcelWorkbookPath",
        "Password",
        "PrivateKey",
        "PublicKey",
        "RecordingRoot",
        "RequestPath",
        "Secret",
        "StationId",
        "Token",
        "TrackingNo",
        "VideoPath",
        "WorkbookPath"
    ];

    private sealed class RedactingTextFormatter : ITextFormatter
    {
        private readonly MessageTemplateTextFormatter _inner;
        private static readonly Regex QuotedWindowsPath = new(
            """(?i)(?<quote>["'])(?:[a-z]:\\|\\\\)[^"'\r\n]+\\k<quote>""",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex UnquotedWindowsPath = new(
            """(?i)(?:[a-z]:\\|\\\\)(?:[^\\/:*?"<>|\r\n]+\\)*[^\\/:*?"<>|\r\n]*""",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex CredentialQuery = new(
            """(?i)(?<prefix>[?&](?:access_token|api[_-]?key|key|password|secret|token)=)[^&\s]+""",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex AuthorizationValue = new(
            """(?i)\b(?:Basic|Bearer|Device)\s+[A-Za-z0-9._~+/=-]{8,}""",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex SensitiveAssignment = new(
            """(?i)\b(?<name>access[_ -]?token|api[_ -]?key|authorization|certificate[_ -]?fingerprint|device[_ -]?id|password|private[_ -]?key|public[_ -]?key|secret|station[_ -]?id|token|tracking(?:[_ -]?(?:no|number))?|waybill)\s*[:=]\s*[^,\s;\r\n}\]]+""",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex LongSecret = new(
            """\b(?:[A-Fa-f0-9]{32,}|[A-Za-z0-9_-]{48,})\b""",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public RedactingTextFormatter(string outputTemplate)
        {
            _inner = new MessageTemplateTextFormatter(outputTemplate, CultureInfo.InvariantCulture);
        }

        public void Format(LogEvent logEvent, TextWriter output)
        {
            using var buffer = new StringWriter(CultureInfo.InvariantCulture);
            _inner.Format(logEvent, buffer);
            var safe = QuotedWindowsPath.Replace(buffer.ToString(), "${quote}[PATH]${quote}");
            safe = UnquotedWindowsPath.Replace(safe, "[PATH]");
            safe = CredentialQuery.Replace(safe, "${prefix}[REDACTED]");
            safe = AuthorizationValue.Replace(safe, "[AUTH REDACTED]");
            safe = SensitiveAssignment.Replace(safe, "${name}=[REDACTED]");
            safe = LongSecret.Replace(safe, "[REDACTED]");
            output.Write(safe);
        }
    }
}
