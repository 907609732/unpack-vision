using System.IO;
using System.Text.Json;
using Velopack;
using Velopack.Sources;

namespace UnpackVision.App;

internal sealed record DesktopUpdateStatus(
    string Message,
    string? AvailableVersion = null,
    int? Progress = null,
    bool ReadyToInstall = false,
    bool IsChecking = false);

internal sealed class DesktopUpdateService
{
    private static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(6);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly UpdateManager _manager;
    private readonly string _statePath;
    private DateTimeOffset? _lastCheckAt;

    internal DesktopUpdateService()
    {
        _manager = new UpdateManager(new GithubSource(ProductInfo.RepositoryUrl, null, false));
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UnpackVision");
        _statePath = Path.Combine(root, "update-state.json");
        _lastCheckAt = ReadLastCheck();
        Status = new DesktopUpdateStatus(
            _manager.IsInstalled
                ? $"当前版本 {ProductInfo.Version}"
                : "当前为便携调试版；安装正式版后可自动更新");
    }

    internal event EventHandler<DesktopUpdateStatus>? StatusChanged;
    internal event EventHandler? UpdateReady;
    internal DesktopUpdateStatus Status { get; private set; }
    internal bool IsInstalled => _manager.IsInstalled;

    internal async Task CheckAndDownloadAsync(bool force, CancellationToken cancellationToken = default)
    {
        if (!_manager.IsInstalled)
        {
            SetStatus(new DesktopUpdateStatus("当前为便携调试版；请从 GitHub 安装正式版以启用自动更新"));
            return;
        }
        if (!force && _lastCheckAt is { } last && DateTimeOffset.Now - last < AutomaticCheckInterval)
        {
            return;
        }
        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            SetStatus(new DesktopUpdateStatus("正在检查更新…", IsChecking: true));
            var update = await _manager.CheckForUpdatesAsync();
            _lastCheckAt = DateTimeOffset.Now;
            SaveLastCheck(_lastCheckAt.Value);
            if (update is null)
            {
                SetStatus(new DesktopUpdateStatus($"当前已是最新版 {ProductInfo.Version}"));
                return;
            }

            var version = update.TargetFullRelease.Version.ToString();
            SetStatus(new DesktopUpdateStatus($"发现新版本 {version}，正在后台下载…", version, 0));
            await _manager.DownloadUpdatesAsync(
                update,
                progress => SetStatus(new DesktopUpdateStatus(
                    $"正在下载版本 {version}：{progress}%",
                    version,
                    progress)),
                cancellationToken);
            SetStatus(new DesktopUpdateStatus(
                $"版本 {version} 已下载，空闲时可以重启更新",
                version,
                100,
                ReadyToInstall: true));
            UpdateReady?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetStatus(new DesktopUpdateStatus("更新检查已取消"));
        }
        catch (Exception exception)
        {
            SetStatus(new DesktopUpdateStatus($"更新检查失败：{exception.Message}"));
        }
        finally
        {
            _gate.Release();
        }
    }

    internal void ApplyAndRestart()
    {
        var pending = _manager.UpdatePendingRestart
            ?? throw new InvalidOperationException("没有已下载并等待安装的更新");
        BackupUserState(pending.Version.ToString());
        _manager.ApplyUpdatesAndRestart(pending);
    }

    private void SetStatus(DesktopUpdateStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    private DateTimeOffset? ReadLastCheck()
    {
        try
        {
            if (!File.Exists(_statePath))
            {
                return null;
            }
            using var document = JsonDocument.Parse(File.ReadAllText(_statePath));
            return document.RootElement.TryGetProperty("lastCheckAt", out var value) &&
                   value.TryGetDateTimeOffset(out var parsed)
                ? parsed
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void SaveLastCheck(DateTimeOffset value)
    {
        var directory = Path.GetDirectoryName(_statePath)!;
        Directory.CreateDirectory(directory);
        var temporary = _statePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { lastCheckAt = value }));
        File.Move(temporary, _statePath, true);
    }

    private static void BackupUserState(string version)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UnpackVision");
        var backup = Path.Combine(root, "Backups", $"Update-{version}-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(backup);
        foreach (var name in new[] { "settings.json", "unpackvision.db" })
        {
            var source = Path.Combine(root, name);
            if (File.Exists(source))
            {
                File.Copy(source, Path.Combine(backup, name), overwrite: false);
            }
        }
    }
}
