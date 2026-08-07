using System.Windows;
using System.Windows.Controls;
using UnpackVision.Core;

namespace UnpackVision.App;

public partial class MainWindow
{
    private async Task ProcessIssueBarcodeAsync(IssueBarcodeMatch match)
    {
        if (_coordinator?.State != RecordingState.Recording || _coordinator.CurrentRecord is null ||
            _repository is null || _recordingBackend is null)
        {
            FooterText.Text = "当前没有正在录像的包裹，异常标签未添加";
            Speak("当前没有正在录像的包裹");
            return;
        }

        if (match.Action == IssueBarcodeAction.UndoLastTag)
        {
            await UndoLastIssueTagAsync();
            return;
        }

        if (match.Tag is null)
        {
            return;
        }
        var record = _coordinator.CurrentRecord;
        var alreadyActive = record.Tags.Any(item => item.IsActive &&
            string.Equals(item.TagId, match.Tag.Id, StringComparison.OrdinalIgnoreCase));
        await _repository.AddTagAsync(record.Id, match.Tag, DateTimeOffset.Now, "scanner", _lifetime.Token);
        record.Tags = await _repository.GetTagsAsync(record.Id, false, _lifetime.Token);
        record.UpdatedAt = DateTimeOffset.Now;
        await _recordingBackend.UpdateIssueOverlayAsync(record.Id, record.Tags, _lifetime.Token);

        if (!alreadyActive && _settings.CaptureSnapshotOnIssueTag)
        {
            try
            {
                var snapshot = await _recordingBackend.TakeSnapshotAsync(_lifetime.Token);
                record.Snapshots = [.. record.Snapshots, snapshot];
                await _repository.UpdateAsync(record, _lifetime.Token);
            }
            catch (Exception ex)
            {
                FooterText.Text = $"标签已保存，但自动截图失败：{ex.Message}";
            }
        }

        UpdateIssueUi(record);
        FooterText.Text = alreadyActive ? $"已经标记：{match.Tag.Name}" : $"已标记：{match.Tag.Name}";
        Speak(alreadyActive ? $"已经标记{match.Tag.Name}" : $"已标记{match.Tag.Name}");
        ScannerInput.Focus();
    }

    private async Task UndoLastIssueTagAsync()
    {
        if (_coordinator?.CurrentRecord is not { } record || _repository is null || _recordingBackend is null)
        {
            return;
        }
        var removed = await _repository.UndoLastTagAsync(record.Id, DateTimeOffset.Now, _lifetime.Token);
        if (removed is null)
        {
            FooterText.Text = "当前录像没有可撤销的异常标签";
            Speak("没有可撤销的异常标签");
            return;
        }
        record.Tags = await _repository.GetTagsAsync(record.Id, false, _lifetime.Token);
        await _recordingBackend.UpdateIssueOverlayAsync(record.Id, record.Tags, _lifetime.Token);
        UpdateIssueUi(record);
        FooterText.Text = $"已撤销标签：{removed.TagName}";
        Speak($"已撤销{removed.TagName}");
        ScannerInput.Focus();
    }

    private void UpdateIssueUi(ScanRecord record)
    {
        var active = record.Tags.Where(item => item.IsActive).OrderBy(item => item.TaggedAt).ToArray();
        ActiveIssueSummaryText.Text = active.Length == 0
            ? "当前没有异常标签"
            : $"异常：{string.Join("、", active.Select(item => item.TagName))}";
        WatermarkIssueText.Text = string.Join("\n", active.Select(item => $"异常：{item.TagName} {item.TaggedAt.LocalDateTime:HH:mm:ss}"));
        _loadingIssueNote = true;
        IssueNoteInput.Text = record.Note;
        _loadingIssueNote = false;
    }

    private async void QuickIssueTagButton_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is IssueTagDefinition tag)
        {
            await ProcessIssueBarcodeAsync(new IssueBarcodeMatch(IssueBarcodeAction.AddTag, tag));
        }
    }

    private async void UndoIssueTagButton_OnClick(object sender, RoutedEventArgs e) =>
        await ProcessIssueBarcodeAsync(new IssueBarcodeMatch(IssueBarcodeAction.UndoLastTag));

    private void IssueNoteInput_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingIssueNote || _coordinator?.State != RecordingState.Recording)
        {
            return;
        }
        _noteSaveTimer.Stop();
        _noteSaveTimer.Start();
    }

    private async void SaveNoteTimer_OnTick(object? sender, EventArgs e)
    {
        _noteSaveTimer.Stop();
        await SaveIssueNoteAsync();
    }

    private async Task FlushIssueNoteAsync()
    {
        _noteSaveTimer.Stop();
        await SaveIssueNoteAsync();
    }

    private async Task SaveIssueNoteAsync()
    {
        if (_coordinator?.CurrentRecord is not { } record || _repository is null || _loadingIssueNote)
        {
            return;
        }
        var note = IssueNoteInput.Text.Trim();
        if (string.Equals(note, record.Note, StringComparison.Ordinal))
        {
            return;
        }
        var now = DateTimeOffset.Now;
        await _repository.UpdateNoteAsync(record.Id, note, now, _lifetime.Token);
        record.Note = note;
        record.NoteUpdatedAt = now;
        FooterText.Text = "备注已保存";
    }
}
