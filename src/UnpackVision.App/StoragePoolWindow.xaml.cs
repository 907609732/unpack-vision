using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using UnpackVision.Core.Recording;

namespace UnpackVision.App;

public partial class StoragePoolWindow : Window
{
    private readonly StoragePoolPresentationController _controller;
    private readonly CancellationTokenSource _lifetimeCancellation = new();

    public StoragePoolWindow(StoragePoolPresentationController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        InitializeComponent();
        DataContext = _controller;
        _controller.Targets.CollectionChanged += Targets_OnCollectionChanged;
        Loaded += StoragePoolWindow_OnLoaded;
        Closed += StoragePoolWindow_OnClosed;
        StorageTargetsList.SelectedIndex = _controller.Targets.Count > 0 ? 0 : -1;
        UpdateCommandState();
    }

    public StoragePoolOptions? SavedOptions { get; private set; }

    private async void StoragePoolWindow_OnLoaded(object sender, RoutedEventArgs e) =>
        await RefreshStorageTargetsAsync(showErrors: false);

    private void StoragePoolWindow_OnClosed(object? sender, EventArgs e)
    {
        _controller.Targets.CollectionChanged -= Targets_OnCollectionChanged;
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
    }

    private async void AddStorageTarget_OnClick(object sender, RoutedEventArgs e)
    {
        var initialDirectory = (StorageTargetsList.SelectedItem as StorageTargetRowViewModel)?.RootPath;
        var dialog = new OpenFolderDialog
        {
            Title = "选择新的录像盘位目录",
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : string.Empty
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var added = _controller.AddTarget(dialog.FolderName);
            StorageTargetsList.SelectedItem = added;
            await RefreshStorageTargetsAsync(showErrors: true);
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(this, exception.Message, "无法添加盘位", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RemoveStorageTarget_OnClick(object sender, RoutedEventArgs e)
    {
        if (StorageTargetsList.SelectedItem is not StorageTargetRowViewModel selected)
        {
            return;
        }
        var choice = MessageBox.Show(
            this,
            $"只从拆包智录的盘位配置中移除“{selected.DisplayName}”。\n\n" +
            $"目录 {selected.RootPath} 中的录像、照片和迁移索引不会被删除。是否继续？",
            "移除录像盘位",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (choice != MessageBoxResult.Yes)
        {
            return;
        }

        var oldIndex = StorageTargetsList.SelectedIndex;
        _controller.RemoveTarget(selected);
        StorageTargetsList.SelectedIndex = _controller.Targets.Count == 0
            ? -1
            : Math.Min(oldIndex, _controller.Targets.Count - 1);
        UpdateCommandState();
    }

    private void MoveStorageTargetUp_OnClick(object sender, RoutedEventArgs e) => MoveSelectedTarget(-1);

    private void MoveStorageTargetDown_OnClick(object sender, RoutedEventArgs e) => MoveSelectedTarget(1);

    private void MoveSelectedTarget(int delta)
    {
        if (StorageTargetsList.SelectedItem is not StorageTargetRowViewModel selected)
        {
            return;
        }
        _controller.MoveTarget(selected, delta);
        StorageTargetsList.SelectedItem = selected;
        StorageTargetsList.ScrollIntoView(selected);
        UpdateCommandState();
    }

    private async void RefreshStorageTargets_OnClick(object sender, RoutedEventArgs e) =>
        await RefreshStorageTargetsAsync(showErrors: true);

    private async Task RefreshStorageTargetsAsync(bool showErrors)
    {
        RefreshStorageTargetsButton.IsEnabled = false;
        try
        {
            await _controller.RefreshAsync(_lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (showErrors)
            {
                MessageBox.Show(
                    this,
                    $"无法完成盘位检查：{exception.Message}",
                    "盘位检查",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        finally
        {
            if (!_lifetimeCancellation.IsCancellationRequested)
            {
                RefreshStorageTargetsButton.IsEnabled = true;
            }
        }
    }

    private void StorageTargetsList_OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateCommandState();

    private void Targets_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        UpdateCommandState();

    private void UpdateCommandState()
    {
        var selectedIndex = StorageTargetsList.SelectedIndex;
        MoveStorageTargetUpButton.IsEnabled = selectedIndex > 0;
        MoveStorageTargetDownButton.IsEnabled = selectedIndex >= 0 && selectedIndex < _controller.Targets.Count - 1;
        RemoveStorageTargetButton.IsEnabled = selectedIndex >= 0;
        EmptyStoragePoolPanel.Visibility = _controller.Targets.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SavedOptions = _controller.BuildOptions();
            DialogResult = true;
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(this, exception.Message, "盘位配置有误", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
