namespace UnpackVision.Tests;

public sealed class CameraProfileDialogCloseTests
{
    [Fact]
    public void ClosingHandlerCompletesImmediateShutdownWithoutNestedClose()
    {
        var code = LoadDialogCode();
        var handler = Slice(
            code,
            "private async void CameraProfileDialog_OnClosing",
            "private bool HasActiveBackgroundOperation");

        Assert.Contains("if (!HasActiveBackgroundOperation())", handler, StringComparison.Ordinal);
        Assert.Contains("ShutdownAsync().GetAwaiter().GetResult();", handler, StringComparison.Ordinal);
        Assert.Contains("_shutdownCompleted = true;", handler, StringComparison.Ordinal);
        Assert.Contains("_closeAllowed = true;", handler, StringComparison.Ordinal);
        Assert.DoesNotContain($"{Environment.NewLine}        Close();", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void ClosingHandlerDefersCloseUntilActivePreviewCancellationCompletes()
    {
        var code = LoadDialogCode();
        var handler = Slice(
            code,
            "private async void CameraProfileDialog_OnClosing",
            "private bool HasActiveBackgroundOperation");
        var deferredClose = Slice(
            code,
            "private void QueueDeferredClose",
            "private void LoadProfile");

        var cancelIndex = handler.IndexOf("e.Cancel = true;", StringComparison.Ordinal);
        var awaitIndex = handler.IndexOf("await ShutdownAsync();", StringComparison.Ordinal);
        var queueIndex = handler.IndexOf("QueueDeferredClose();", StringComparison.Ordinal);
        Assert.True(cancelIndex >= 0 && cancelIndex < awaitIndex && awaitIndex < queueIndex);
        Assert.Contains("Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle", deferredClose, StringComparison.Ordinal);
        Assert.Contains("_closeAllowed = true;", deferredClose, StringComparison.Ordinal);
        Assert.Contains("Close();", deferredClose, StringComparison.Ordinal);
    }

    [Fact]
    public void ShutdownTracksPreviewTransitionsAndRechecksWindowAfterStoppingOldPreview()
    {
        var code = LoadDialogCode();
        var activeCheck = Slice(
            code,
            "private bool HasActiveBackgroundOperation",
            "private bool HasActivePreviewTransition");
        var shutdown = Slice(
            code,
            "private async Task ShutdownCoreAsync",
            "private async Task AwaitPreviewTransitionsAsync");
        var restart = Slice(
            code,
            "private async Task RestartCameraPreviewAsync",
            "private async Task RunPreviewSessionAsync");

        Assert.Contains("HasActivePreviewTransition()", activeCheck, StringComparison.Ordinal);
        Assert.Contains("await AwaitPreviewTransitionsAsync();", shutdown, StringComparison.Ordinal);
        Assert.True(
            shutdown.IndexOf("await AwaitPreviewTransitionsAsync();", StringComparison.Ordinal) <
            shutdown.IndexOf("_lifetime.Dispose();", StringComparison.Ordinal));

        var stopIndex = restart.IndexOf("await StopCameraPreviewAsync(updateStatus: false);", StringComparison.Ordinal);
        var recheckIndex = restart.IndexOf("if (!CanStartPreviewTransition())", stopIndex + 1, StringComparison.Ordinal);
        var tokenIndex = restart.IndexOf("CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token)", StringComparison.Ordinal);
        Assert.True(stopIndex >= 0 && recheckIndex > stopIndex && tokenIndex > recheckIndex);
    }

    private static string LoadDialogCode() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "CameraProfileDialog.xaml.cs"));

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing source marker: {endMarker}");
        return source[start..end];
    }
}
