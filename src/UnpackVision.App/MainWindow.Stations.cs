using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Threading;
using UnpackVision.Application.Scanning;
using UnpackVision.Core;

namespace UnpackVision.App;

public partial class MainWindow
{
    private async void OnStationStateTimer(object? sender, EventArgs e) =>
        await PollStationStateAsync();

    private async Task PollStationStateAsync()
    {
        if (_stationStatePollActive || _repository is null || _lifetime.IsCancellationRequested)
        {
            return;
        }

        _stationStatePollActive = true;
        try
        {
            var stationId = Uri.EscapeDataString(Environment.MachineName);
            var snapshot = await StationHostConnection.Http.GetFromJsonAsync<StationStateSnapshot>(
                $"/api/v1/stations/{stationId}/state",
                StationHostConnection.JsonOptions,
                _lifetime.Token);
            if (snapshot is null)
            {
                return;
            }

            var localRecording = _coordinator?.State is RecordingState.Starting or RecordingState.Recording or RecordingState.Saving;
            switch (snapshot.RecordingState)
            {
                case RecordingState.Starting when !localRecording:
                    CurrentStateText.Text = "手机指令 · 正在启动录像";
                    StateDot.Fill = Brushes.Orange;
                    FooterText.Text = snapshot.TrackingNo is { Length: > 0 }
                        ? $"已收到手机扫码：{snapshot.TrackingNo}"
                        : "已收到手机扫码，正在启动录像";
                    break;

                case RecordingState.Recording when !localRecording && snapshot.RecordId is { } recordId:
                    if (_mirroredStationRecordId != recordId)
                    {
                        var record = await _repository.GetAsync(recordId, _lifetime.Token);
                        if (record is not null)
                        {
                            _mirroredStationRecordId = recordId;
                            ShowRecordingUi(record);
                            FooterText.Text = $"手机扫码已触发录像：{record.TrackingNo}";
                            Speak("手机扫码，开始录制");
                        }
                    }
                    break;

                case RecordingState.Saving when _mirroredStationRecordId is not null:
                    CurrentStateText.Text = "手机指令 · 正在保存";
                    StateDot.Fill = Brushes.Orange;
                    FooterText.Text = "手机已发出结束指令，正在保存录像";
                    break;

                case RecordingState.Idle when _mirroredStationRecordId is not null:
                case RecordingState.Completed when _mirroredStationRecordId is not null:
                case RecordingState.Failed when _mirroredStationRecordId is not null:
                    var failed = snapshot.RecordingState == RecordingState.Failed;
                    _mirroredStationRecordId = null;
                    ShowIdleUi(failed ? "手机指令录像失败，请查看全部记录" : "手机指令录像已保存，可以继续扫描");
                    Speak(failed ? "录像失败，请检查记录" : "录像已保存");
                    await RefreshRecentAsync();
                    break;
            }
        }
        catch (HttpRequestException)
        {
            if (_mirroredStationRecordId is not null)
            {
                FooterText.Text = "工位主机连接中断，正在自动重连";
            }
        }
        catch (JsonException)
        {
            FooterText.Text = "工位主机状态格式异常，正在自动重试";
        }
        catch (TaskCanceledException) when (!_lifetime.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _stationStatePollActive = false;
        }
    }

    private void RebuildStationRouter()
    {
        if (_coordinator is null || _repository is null || _scanCommandLedger is null)
        {
            return;
        }
        _stationRouter = new StationScanCommandRouter(
            _coordinator,
            _repository,
            new SystemClock(),
            _settings.Scanner,
            Environment.MachineName,
            "excel",
            _settings.IssueTags,
            _scanCommandLedger);
    }

    private async Task<ScanAcknowledgement> RouteMobileCommandAsync(
        ScanCommand command,
        CancellationToken cancellationToken)
    {
        if (_stationRouter is null)
        {
            throw new InvalidOperationException("桌面录像核心尚未就绪");
        }
        return await Dispatcher.InvokeAsync(
            async () =>
            {
                var acknowledgement = await _stationRouter.RouteAsync(command, cancellationToken);
                ApplyMobileCommandAcknowledgement(command, acknowledgement);
                return acknowledgement;
            },
            DispatcherPriority.Normal).Task.Unwrap();
    }

    private void ApplyMobileCommandAcknowledgement(ScanCommand command, ScanAcknowledgement acknowledgement)
    {
        if (command.Mode != DeviceOperatingMode.IssueRemote)
        {
            return;
        }

        if (_coordinator?.CurrentRecord is { } current)
        {
            UpdateIssueUi(current);
        }

        switch (acknowledgement.Action)
        {
            case ScanCommandAction.IssueTagged:
            case ScanCommandAction.IssueUndone:
            case ScanCommandAction.NoteUpdated:
            case ScanCommandAction.SnapshotCaptured:
            case ScanCommandAction.Rejected:
            case ScanCommandAction.Failed:
            case ScanCommandAction.Ignored:
                Speak(acknowledgement.Message);
                break;
        }
    }

    private StationStateSnapshot GetDesktopStationState() => new(
        Environment.MachineName,
        _coordinator?.State ?? RecordingState.Idle,
        _coordinator?.CurrentRecord?.Id,
        _coordinator?.CurrentRecord?.TrackingNo,
        DateTimeOffset.Now);
}
