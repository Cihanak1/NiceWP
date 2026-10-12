using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using NiceWP.App.Tray;
using NiceWP.Core;
using NiceWP.Infrastructure.Autostart;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Library;
using NiceWP.Infrastructure.Settings;
using NiceWP.Infrastructure.Tray;

namespace NiceWP.App;

public sealed partial class DesktopTestWindow : Window
{
    private readonly RuntimeClient runtimeClient = new();
    private readonly TextWriterTraceListener? diagnostics;
    private readonly AppTrayIcon trayIcon;
    private readonly TrayLifecycleController controller;
    private readonly AutostartManager autostartManager = new();
    private readonly SettingsService settingsService = new();
    private readonly WallpaperThumbnailService thumbnailService = new();
    private readonly WallpaperLibraryController libraryController;
    private readonly HashSet<Border> realizedCardBorders = [];
    private readonly List<(string Id, string FilePath, string Type, string DisplayName, bool IsMissing)> renderedLibrarySnapshot = [];
    private string? activeWallpaperPath;
    private string activeWallpaperType = IpcConstants.WallpaperTypeNone;
    private bool activeWallpaperPaused;
    private string activePlaybackState = IpcConstants.PlaybackStateNone;
    private bool closed;
    private bool selecting;
    private bool libraryBusy;
    private int libraryLoads;
    private bool isSyncingAutostart;
    private bool isSyncingAppSettings;

    public DesktopTestWindow(bool isStartup = false)
    {
        InitializeComponent();
        StatusText.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => UpdateFeedbackVisuals());
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1060, 720));
        AppWindow.Closing += OnClosing;
        Closed += OnClosed;
        Activated += OnActivated;

        if (isStartup)
        {
            AppWindow.Hide();
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero)
        {
            hwnd = (IntPtr)AppWindow.Id.Value;
        }

        libraryController = new WallpaperLibraryController(
            ipcClient: runtimeClient.IpcClient,
            hostLauncher: runtimeClient.Launcher);

        controller = new TrayLifecycleController(
            runtimeClient.IpcClient,
            initialWindowVisible: !isStartup,
            restoreWindowAction: RestoreAndShowWindow,
            hideWindowAction: () => AppWindow.Hide(),
            exitAppAction: PerformAppExit,
            syncStatusAction: SyncRuntimeStatusAsync);

        trayIcon = new AppTrayIcon(hwnd);
        trayIcon.OpenRequested += OnTrayOpenRequested;
        trayIcon.StopWallpaperRequested += OnTrayStopWallpaperRequested;
        trayIcon.ExitRequested += OnTrayExitRequested;
        trayIcon.Initialize();

        try
        {
            diagnostics = new TextWriterTraceListener(Path.Combine(Path.GetTempPath(), $"NiceWP-image-test-{Environment.ProcessId}.log"));
            Trace.Listeners.Add(diagnostics);
            Trace.AutoFlush = true;
            Trace.WriteLine($"NiceWP session started: pid={Environment.ProcessId}");
        }
        catch (Exception exception)
        {
            Trace.WriteLine(exception);
            StatusText.Text = "Diagnostic log could not be opened.";
        }

        LibraryStoragePathText.Text = $"Library file: {libraryController.Service.FilePath}";
        SettingsStoragePathText.Text = $"Settings file: {settingsService.FilePath}";

        Navigation.SelectedItem = LibraryNavigationItem;
        RefreshAutostartState();

        _ = LoadAppSettingsAsync();
        _ = LoadLibraryAsync();
        _ = SyncRuntimeStatusAsync();
    }

    public async Task LoadAppSettingsAsync()
    {
        try
        {
            var settings = await settingsService.LoadAsync();
            if (closed) return;

            isSyncingAppSettings = true;
            try
            {
                RestoreLastWallpaperCheckBox.IsChecked = settings.RestoreLastWallpaper;
                ApplyThemePreference(settings.Theme);
                SelectThemeComboBoxItem(settings.Theme);
            }
            finally
            {
                isSyncingAppSettings = false;
            }
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Error loading application settings: {exception}");
        }
    }

    private bool IsInteractionBusy => libraryBusy || selecting || libraryController.IsBusy;

    public async Task LoadLibraryAsync()
    {
        libraryLoads++;
        UpdateFeedbackVisuals();
        try
        {
            await libraryController.LoadAsync();
            if (closed) return;
            RenderLibraryItems();
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Error loading wallpaper library: {exception}");
            if (!closed)
            {
                StatusText.Text = "Failed to load library.";
                RenderLibraryItems();
            }
        }
        finally
        {
            libraryLoads--;
            if (!closed) UpdateFeedbackVisuals();
        }
    }

    public async Task SyncRuntimeStatusAsync()
    {
        try
        {
            var ensure = await runtimeClient.EnsureHostRunningAsync();
            if (closed) return;

            if (!ensure.Success)
            {
                StatusText.Text = "Runtime unavailable.";
                UpdateActiveDetails(statusKnown: false);
                return;
            }

            var status = await runtimeClient.GetStatusAsync();
            if (closed) return;

            if (status.Success && status.HasActiveWallpaper && !string.IsNullOrWhiteSpace(status.ActivePath))
            {
                var previousPath = activeWallpaperPath;
                activeWallpaperPath = status.ActivePath;
                activeWallpaperType = status.WallpaperType;
                activeWallpaperPaused = status.IsPaused;
                activePlaybackState = status.PlaybackState;
                controller.SetActiveWallpaper(activeWallpaperPath, activeWallpaperType);
                UpdateActiveDetails(status.IsPaused, status.PlaybackState, statusKnown: true);

                if (!string.Equals(previousPath, activeWallpaperPath, StringComparison.OrdinalIgnoreCase))
                {
                    RenderLibraryItems();
                }
            }
            else
            {
                var hadWallpaper = !string.IsNullOrWhiteSpace(activeWallpaperPath);
                activeWallpaperPath = null;
                activeWallpaperType = IpcConstants.WallpaperTypeNone;
                activeWallpaperPaused = false;
                activePlaybackState = IpcConstants.PlaybackStateNone;
                controller.SetActiveWallpaper(null, IpcConstants.WallpaperTypeNone);
                UpdateActiveDetails(statusKnown: status.Success);
                if (!status.Success)
                {
                    StatusText.Text = "Runtime unavailable.";
                }

                if (hadWallpaper)
                {
                    RenderLibraryItems();
                }
            }
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Error synchronizing runtime status: {exception}");
            if (!closed)
            {
                StatusText.Text = "Runtime unavailable.";
                UpdateActiveDetails(statusKnown: false);
            }
        }
    }

    private static string FormatWallpaperDisplay(string? path, string? wallpaperType = null)
    {
        if (string.IsNullOrWhiteSpace(path) || string.Equals(wallpaperType, IpcConstants.WallpaperTypeNone, StringComparison.OrdinalIgnoreCase))
        {
            return "No wallpaper active";
        }

        return Path.GetFileName(path);
    }

    private async Task<PickFileResult?> PickWallpaperFileAsync()
    {
        var picker = new FileOpenPicker(AppWindow.Id);
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".mp4");
        return await picker.PickSingleFileAsync();
    }

    private async void OnSelectImage(object sender, RoutedEventArgs args)
    {
        if (IsInteractionBusy || closed) return;
        selecting = true;
        UpdateLibraryActionState();
        try
        {
            var file = await PickWallpaperFileAsync();
            if (file == null || closed) return;

            var validation = WallpaperLibraryService.ValidateWallpaperFile(file.Path, out var validatedPath, out var wallpaperType);
            if (!validation.Success)
            {
                StatusText.Text = WallpaperLibraryController.MapUserStatusMessage(validation.Error, validation.Message);
                return;
            }

            StatusText.Text = $"Applying wallpaper: {Path.GetFileName(validatedPath)}...";

            var ensure = await runtimeClient.EnsureHostRunningAsync();
            if (closed) return;
            if (!ensure.Success)
            {
                StatusText.Text = "Runtime unavailable.";
                UpdateActiveDetails(statusKnown: false);
                return;
            }

            var isVideo = string.Equals(wallpaperType, IpcConstants.WallpaperTypeVideo, StringComparison.OrdinalIgnoreCase);
            var result = isVideo
                ? await runtimeClient.SetVideoAsync(validatedPath)
                : await runtimeClient.SetImageAsync(validatedPath);

            if (closed) return;

            if (result.Success)
            {
                activeWallpaperPath = validatedPath;
                activeWallpaperType = isVideo ? IpcConstants.WallpaperTypeVideo : IpcConstants.WallpaperTypeImage;
                activeWallpaperPaused = false;
                activePlaybackState = IpcConstants.PlaybackStateRunning;
                controller.SetActiveWallpaper(activeWallpaperPath, activeWallpaperType);
                await SyncRuntimeStatusAsync();
                RenderLibraryItems();
                StatusText.Text = $"Applied wallpaper: {Path.GetFileName(validatedPath)}";
            }
            else
            {
                StatusText.Text = WallpaperLibraryController.MapIpcFailureStatusMessage(result.Message, "Failed to apply wallpaper.");
                UpdateActiveDetails(activeWallpaperPaused, activePlaybackState);
            }
        }
        catch (Exception exception)
        {
            Trace.WriteLine(exception);
            if (!closed) StatusText.Text = "Failed to apply wallpaper.";
        }
        finally
        {
            selecting = false;
            if (!closed)
            {
                UpdateLibraryActionState();
            }
        }
    }

    private async void OnAddWallpaper(object sender, RoutedEventArgs args)
    {
        if (IsInteractionBusy || closed) return;
        libraryBusy = true;
        UpdateLibraryActionState();
        try
        {
            var file = await PickWallpaperFileAsync();
            if (file == null || closed) return;

            var result = await libraryController.AddFileAsync(file.Path);
            if (closed) return;

            RenderLibraryItems();
            StatusText.Text = result.Success
                ? (result.Message ?? string.Empty)
                : WallpaperLibraryController.MapUserStatusMessage(result.Error, result.Message);
        }
        catch (Exception exception)
        {
            Trace.WriteLine(exception);
            if (!closed)
            {
                StatusText.Text = "Failed to load library.";
            }
        }
        finally
        {
            libraryBusy = false;
            if (!closed)
            {
                UpdateLibraryActionState();
            }
        }
    }

    private async void OnApplyWallpaper(object sender, RoutedEventArgs args)
    {
        if (IsInteractionBusy || closed) return;
        if (sender is FrameworkElement { DataContext: WallpaperLibraryEntryView entry })
        {
            libraryController.SelectItem(entry.Id);
        }

        libraryBusy = true;
        UpdateLibraryActionState();
        try
        {
            var selectedName = libraryController.SelectedItem?.DisplayName;
            if (!string.IsNullOrWhiteSpace(selectedName))
            {
                StatusText.Text = $"Applying wallpaper: {selectedName}...";
            }

            var result = await libraryController.ApplySelectedAsync();
            if (closed) return;

            if (result.Success)
            {
                activeWallpaperPath = result.ActiveWallpaperPath;
                activeWallpaperType = result.ActiveWallpaperType;
                activeWallpaperPaused = result.IsPaused;
                activePlaybackState = result.PlaybackState;
                controller.SetActiveWallpaper(activeWallpaperPath, activeWallpaperType);
                UpdateActiveDetails(result.IsPaused, result.PlaybackState, statusKnown: true);
                RenderLibraryItems();
                StatusText.Text = $"Applied wallpaper: {FormatWallpaperDisplay(activeWallpaperPath, activeWallpaperType)}";
            }
            else
            {
                RenderLibraryItems();
                UpdateActiveDetails(
                    activeWallpaperPaused,
                    activePlaybackState,
                    statusKnown: result.Error != WallpaperError.DesktopHostUnavailable);
                StatusText.Text = WallpaperLibraryController.MapUserStatusMessage(result.Error, result.Message);
            }
        }
        catch (Exception exception)
        {
            Trace.WriteLine(exception);
            if (!closed)
            {
                StatusText.Text = "Failed to apply wallpaper.";
            }
        }
        finally
        {
            libraryBusy = false;
            if (!closed)
            {
                UpdateLibraryActionState();
            }
        }
    }

    private async void OnRemoveWallpaper(object sender, RoutedEventArgs args)
    {
        if (IsInteractionBusy || closed) return;
        string? removedName = null;
        if (sender is FrameworkElement { DataContext: WallpaperLibraryEntryView entry })
        {
            removedName = entry.DisplayName;
            libraryController.SelectItem(entry.Id);
        }

        libraryBusy = true;
        UpdateLibraryActionState();
        try
        {
            var result = await libraryController.RemoveSelectedAsync();
            if (closed) return;

            RenderLibraryItems();
            StatusText.Text = result.Success
                ? (string.IsNullOrWhiteSpace(removedName) ? "Removed from library." : $"Removed from library: {removedName}")
                : WallpaperLibraryController.MapUserStatusMessage(result.Error, result.Message);
        }
        catch (Exception exception)
        {
            Trace.WriteLine(exception);
            if (!closed)
            {
                StatusText.Text = "Failed to load library.";
            }
        }
        finally
        {
            libraryBusy = false;
            if (!closed)
            {
                UpdateLibraryActionState();
            }
        }
    }

    private async void OnPauseResumeWallpaper(object sender, RoutedEventArgs args)
    {
        if (IsInteractionBusy || closed || string.IsNullOrWhiteSpace(activeWallpaperPath)) return;
        libraryBusy = true;
        UpdateLibraryActionState();
        try
        {
            var ensure = await runtimeClient.EnsureHostRunningAsync();
            if (closed) return;
            if (!ensure.Success)
            {
                StatusText.Text = "Runtime unavailable.";
                UpdateActiveDetails(statusKnown: false);
                return;
            }

            var isCurrentlyPaused = activeWallpaperPaused ||
                string.Equals(activePlaybackState, IpcConstants.PlaybackStatePaused, StringComparison.OrdinalIgnoreCase);

            var response = isCurrentlyPaused
                ? await runtimeClient.IpcClient.ResumeWallpaperAsync()
                : await runtimeClient.IpcClient.PauseWallpaperAsync();

            if (closed) return;

            if (response.Success)
            {
                await SyncRuntimeStatusAsync();
                StatusText.Text = isCurrentlyPaused ? "Wallpaper resumed." : "Wallpaper paused.";
            }
            else
            {
                StatusText.Text = WallpaperLibraryController.MapIpcFailureStatusMessage(response.Message, "Runtime unavailable.");
            }
        }
        catch (Exception exception)
        {
            Trace.WriteLine(exception);
            if (!closed)
            {
                StatusText.Text = "Runtime unavailable.";
            }
        }
        finally
        {
            libraryBusy = false;
            if (!closed)
            {
                UpdateLibraryActionState();
            }
        }
    }

    private async void OnStopActiveWallpaper(object sender, RoutedEventArgs args)
    {
        if (IsInteractionBusy || closed) return;
        libraryBusy = true;
        UpdateLibraryActionState();
        try
        {
            var ensure = await runtimeClient.EnsureHostRunningAsync();
            if (closed) return;
            if (!ensure.Success)
            {
                StatusText.Text = "Runtime unavailable.";
                UpdateActiveDetails(statusKnown: false);
                return;
            }

            var result = await controller.RequestStopWallpaperAsync();
            if (closed) return;

            if (result.Success)
            {
                activeWallpaperPath = null;
                activeWallpaperType = IpcConstants.WallpaperTypeNone;
                activeWallpaperPaused = false;
                activePlaybackState = IpcConstants.PlaybackStateNone;
                UpdateActiveDetails();
                RenderLibraryItems();
                StatusText.Text = "Wallpaper stopped.";
            }
            else
            {
                StatusText.Text = WallpaperLibraryController.MapIpcFailureStatusMessage(result.Message, "Runtime unavailable.");
            }
        }
        catch (Exception exception)
        {
            Trace.WriteLine(exception);
            if (!closed)
            {
                StatusText.Text = "Runtime unavailable.";
            }
        }
        finally
        {
            libraryBusy = false;
            if (!closed)
            {
                UpdateLibraryActionState();
            }
        }
    }

    private async void OnRefreshStatus(object sender, RoutedEventArgs args)
    {
        if (IsInteractionBusy || closed) return;
        libraryBusy = true;
        UpdateLibraryActionState();
        try
        {
            await LoadLibraryAsync();
            await SyncRuntimeStatusAsync();
        }
        finally
        {
            libraryBusy = false;
            if (!closed)
            {
                UpdateLibraryActionState();
            }
        }
    }

    private async void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (LibraryPage == null || ActivePage == null || SettingsPage == null) return;
        var page = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        LibraryPage.Visibility = page == "Library" ? Visibility.Visible : Visibility.Collapsed;
        ActivePage.Visibility = page == "Active" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;

        if (page == "Active" && controller != null)
        {
            await SyncRuntimeStatusAsync();
        }
        else if (page == "Settings")
        {
            RefreshAutostartState();
            await LoadAppSettingsAsync();
        }
    }

    private void OnGalleryLoaded(object sender, RoutedEventArgs args) => ResizeGallery();

    private void OnGallerySizeChanged(object sender, SizeChangedEventArgs args) => ResizeGallery();

    private void ResizeGallery()
    {
        if (LibraryListView.ItemsPanelRoot is ItemsWrapGrid panel)
        {
            var availableWidth = Math.Max(1, LibraryListView.ActualWidth - 8);
            var columns = Math.Max(1, (int)(availableWidth / 252));
            var computedWidth = Math.Floor(availableWidth / columns);
            panel.ItemWidth = columns == 1
                ? Math.Min(availableWidth, 420)
                : Math.Clamp(computedWidth, 220, 320);
        }
    }

    private void OnCardBorderLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Border cardBorder)
        {
            cardBorder.Unloaded -= OnCardBorderUnloaded;
            cardBorder.Unloaded += OnCardBorderUnloaded;
            realizedCardBorders.Add(cardBorder);
            ConfigureCardVisuals(cardBorder);
        }
    }

    private void OnCardBorderUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is Border cardBorder)
        {
            cardBorder.Unloaded -= OnCardBorderUnloaded;
            realizedCardBorders.Remove(cardBorder);
        }
    }

    private void OnCardBorderDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) =>
        ConfigureCardVisuals(sender as Border);

    private void ConfigureCardVisuals(Border? cardBorder)
    {
        if (cardBorder == null || cardBorder.DataContext is not WallpaperLibraryEntryView entry)
        {
            return;
        }

        UpdateCardStateVisuals(cardBorder, entry);

        if (cardBorder.FindName("CardPlaceholderIcon") is SymbolIcon placeholderIcon)
        {
            placeholderIcon.Symbol = entry.IsVideo ? Symbol.Video : Symbol.Pictures;
        }

        if (cardBorder.FindName("CardPreviewImage") is Image previewImage)
        {
            _ = LoadWallpaperPreviewAsync(
                previewImage,
                (entry.IsImage || entry.IsVideo) && !entry.IsMissing ? entry.FilePath : null,
                isVideo: entry.IsVideo);
        }
    }

    private void UpdateCardStateVisuals(Border cardBorder, WallpaperLibraryEntryView entry)
    {
        var currentEntry = libraryController.Items.FirstOrDefault(item =>
            string.Equals(item.Id, entry.Id, StringComparison.OrdinalIgnoreCase)) ?? entry;

        var isActive = !string.IsNullOrWhiteSpace(activeWallpaperPath) &&
            string.Equals(currentEntry.FilePath, activeWallpaperPath, StringComparison.OrdinalIgnoreCase);

        if (cardBorder.FindName("CardStateBadge") is Border badge &&
            cardBorder.FindName("CardStateBadgeText") is TextBlock badgeText)
        {
            if (currentEntry.IsMissing)
            {
                badge.Visibility = Visibility.Visible;
                badgeText.Text = isActive ? "Active · Missing" : "Missing";
            }
            else if (isActive)
            {
                badge.Visibility = Visibility.Visible;
                badgeText.Text = "Active";
            }
            else
            {
                badge.Visibility = Visibility.Collapsed;
            }
        }

        var isIdle = !IsInteractionBusy;
        if (cardBorder.FindName("CardApplyButton") is Button applyButton)
        {
            applyButton.IsEnabled = currentEntry.CanApply && isIdle;
        }

        if (cardBorder.FindName("CardRemoveButton") is Button removeButton)
        {
            removeButton.IsEnabled = currentEntry.CanRemove && isIdle;
        }
    }

    private void OnApplyButtonLoaded(object sender, RoutedEventArgs args) =>
        ConfigureApplyButton(sender as Button);

    private void OnApplyButtonDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) =>
        ConfigureApplyButton(sender as Button);

    private void ConfigureApplyButton(Button? button)
    {
        if (button == null || button.DataContext is not WallpaperLibraryEntryView entry)
        {
            return;
        }

        var currentEntry = libraryController.Items.FirstOrDefault(item =>
            string.Equals(item.Id, entry.Id, StringComparison.OrdinalIgnoreCase)) ?? entry;

        button.IsEnabled = currentEntry.CanApply && !IsInteractionBusy;
        AutomationProperties.SetName(button, $"Apply {currentEntry.DisplayName}");
    }

    private void OnRemoveButtonLoaded(object sender, RoutedEventArgs args) =>
        ConfigureRemoveButton(sender as Button);

    private void OnRemoveButtonDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) =>
        ConfigureRemoveButton(sender as Button);

    private void ConfigureRemoveButton(Button? button)
    {
        if (button == null || button.DataContext is not WallpaperLibraryEntryView entry)
        {
            return;
        }

        var currentEntry = libraryController.Items.FirstOrDefault(item =>
            string.Equals(item.Id, entry.Id, StringComparison.OrdinalIgnoreCase)) ?? entry;

        button.IsEnabled = currentEntry.CanRemove && !IsInteractionBusy;
        AutomationProperties.SetName(button, $"Remove {currentEntry.DisplayName}");
    }

    private async Task LoadWallpaperPreviewAsync(Image targetImage, string? filePath, bool isVideo)
    {
        if (!closed &&
            !string.IsNullOrWhiteSpace(filePath) &&
            string.Equals(targetImage.Tag as string, filePath, StringComparison.OrdinalIgnoreCase) &&
            targetImage.Source != null &&
            targetImage.Visibility == Visibility.Visible &&
            File.Exists(filePath))
        {
            return;
        }

        targetImage.Tag = filePath;
        targetImage.Source = null;
        targetImage.Visibility = Visibility.Collapsed;

        if (closed || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return;
        }

        try
        {
            var previewImagePath = filePath;
            if (isVideo)
            {
                var cachedThumbnailPath = await Task.Run(() =>
                    thumbnailService.GetOrCreateVideoThumbnailAsync(filePath));
                if (closed || !string.Equals(targetImage.Tag as string, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(cachedThumbnailPath) || !File.Exists(cachedThumbnailPath))
                {
                    return;
                }

                previewImagePath = cachedThumbnailPath;
            }

            var bitmap = new BitmapImage
            {
                DecodePixelWidth = 320
            };

            await using var fileStream = new FileStream(
                previewImagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            await bitmap.SetSourceAsync(fileStream.AsRandomAccessStream());

            if (closed || !string.Equals(targetImage.Tag as string, filePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            targetImage.Source = bitmap;
            targetImage.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to load wallpaper preview for '{filePath}': {ex.Message}");
            if (!closed && string.Equals(targetImage.Tag as string, filePath, StringComparison.OrdinalIgnoreCase))
            {
                targetImage.Source = null;
                targetImage.Visibility = Visibility.Collapsed;
            }
        }
    }

    private bool HasLibrarySnapshotChanged(IReadOnlyList<WallpaperLibraryEntryView> items)
    {
        if (LibraryListView.ItemsSource == null || renderedLibrarySnapshot.Count != items.Count)
        {
            return true;
        }

        for (var i = 0; i < items.Count; i++)
        {
            var current = items[i];
            var previous = renderedLibrarySnapshot[i];
            if (!string.Equals(current.Id, previous.Id, StringComparison.Ordinal) ||
                !string.Equals(current.FilePath, previous.FilePath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(current.Type, previous.Type, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(current.DisplayName, previous.DisplayName, StringComparison.Ordinal) ||
                current.IsMissing != previous.IsMissing)
            {
                return true;
            }
        }

        return false;
    }

    private void UpdateRenderedLibrarySnapshot(IReadOnlyList<WallpaperLibraryEntryView> items)
    {
        renderedLibrarySnapshot.Clear();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            renderedLibrarySnapshot.Add((item.Id, item.FilePath, item.Type, item.DisplayName, item.IsMissing));
        }
    }

    private void RenderLibraryItems()
    {
        if (closed) return;

        var items = libraryController.Items;
        var count = items.Count;
        var missingCount = 0;
        for (var i = 0; i < count; i++)
        {
            if (items[i].IsMissing)
            {
                missingCount++;
            }
        }

        LibraryCountText.Text = count switch
        {
            0 => "0 wallpapers in collection",
            1 => missingCount > 0 ? "1 wallpaper in collection (1 missing on disk)" : "1 wallpaper in collection",
            _ => missingCount > 0 ? $"{count} wallpapers in collection ({missingCount} missing on disk)" : $"{count} wallpapers in collection"
        };

        if (HasLibrarySnapshotChanged(items))
        {
            UpdateRenderedLibrarySnapshot(items);
            LibraryListView.ItemsSource = null;
            LibraryListView.ItemsSource = items;
        }
        else
        {
            foreach (var cardBorder in realizedCardBorders)
            {
                if (cardBorder.DataContext is WallpaperLibraryEntryView entry)
                {
                    UpdateCardStateVisuals(cardBorder, entry);
                }
            }
        }

        var hasItems = count > 0;
        LibraryEmptyText.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        LibraryListView.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        UpdateLibraryActionState();
    }

    private void UpdateFeedbackVisuals()
    {
        if (closed) return;
        if (libraryController == null)
        {
            FeedbackArea.Visibility = string.IsNullOrWhiteSpace(StatusText.Text) ? Visibility.Collapsed : Visibility.Visible;
            return;
        }
        var busy = libraryLoads > 0 || IsInteractionBusy;
        BusyIndicator.IsActive = busy;
        BusyIndicator.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = libraryLoads > 0 && !IsInteractionBusy ? "Loading library…" : "Working…";
        FeedbackArea.Visibility = busy || !string.IsNullOrWhiteSpace(StatusText.Text)
            ? Visibility.Visible : Visibility.Collapsed;
        LibraryEmptyText.Visibility = libraryLoads == 0 && libraryController.Items.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateLibraryActionState()
    {
        if (closed) return;
        UpdateFeedbackVisuals();
        var isIdle = !IsInteractionBusy;
        var hasWallpaper = !string.IsNullOrWhiteSpace(activeWallpaperPath);

        AddWallpaperButton.IsEnabled = isIdle;
        LibraryListView.IsEnabled = isIdle;
        SelectImageButton.IsEnabled = isIdle;
        PauseResumeWallpaperButton.IsEnabled = isIdle && hasWallpaper;
        StopActiveWallpaperButton.IsEnabled = isIdle && hasWallpaper;
        RefreshActiveStatusButton.IsEnabled = isIdle;

        foreach (var cardBorder in realizedCardBorders)
        {
            if (cardBorder.DataContext is WallpaperLibraryEntryView boundEntry)
            {
                var currentEntry = libraryController.Items.FirstOrDefault(item =>
                    string.Equals(item.Id, boundEntry.Id, StringComparison.OrdinalIgnoreCase)) ?? boundEntry;

                if (cardBorder.FindName("CardApplyButton") is Button applyButton)
                {
                    applyButton.IsEnabled = currentEntry.CanApply && isIdle;
                }

                if (cardBorder.FindName("CardRemoveButton") is Button removeButton)
                {
                    removeButton.IsEnabled = currentEntry.CanRemove && isIdle;
                }
            }
        }
    }

    private void UpdateActiveDetails(bool isPaused = false, string? playbackState = null, bool statusKnown = true)
    {
        if (closed) return;

        var hasWallpaper = !string.IsNullOrWhiteSpace(activeWallpaperPath) &&
            !string.Equals(activeWallpaperType, IpcConstants.WallpaperTypeNone, StringComparison.OrdinalIgnoreCase);
        var isMissingOnDisk = hasWallpaper && !File.Exists(activeWallpaperPath);
        var isVideo = hasWallpaper && string.Equals(activeWallpaperType, IpcConstants.WallpaperTypeVideo, StringComparison.OrdinalIgnoreCase);
        var isImage = hasWallpaper && string.Equals(activeWallpaperType, IpcConstants.WallpaperTypeImage, StringComparison.OrdinalIgnoreCase);

        PathText.Text = FormatWallpaperDisplay(activeWallpaperPath, activeWallpaperType);
        ToolTipService.SetToolTip(PathText, hasWallpaper ? activeWallpaperPath : null);

        ActiveFullPathText.Text = hasWallpaper
            ? activeWallpaperPath!
            : "Select a wallpaper from the Library or choose a file below.";
        ActiveMissingWarningText.Visibility = isMissingOnDisk ? Visibility.Visible : Visibility.Collapsed;
        ActiveEmptyState.Visibility = hasWallpaper ? Visibility.Collapsed : Visibility.Visible;
        ActiveDetailsPanel.Visibility = hasWallpaper ? Visibility.Visible : Visibility.Collapsed;

        WallpaperTypeText.Text = hasWallpaper ? activeWallpaperType : "None";

        var resolvedPaused = isPaused ||
            string.Equals(playbackState, IpcConstants.PlaybackStatePaused, StringComparison.OrdinalIgnoreCase);
        activeWallpaperPaused = resolvedPaused;
        activePlaybackState = hasWallpaper
            ? (resolvedPaused ? IpcConstants.PlaybackStatePaused : IpcConstants.PlaybackStateRunning)
            : IpcConstants.PlaybackStateNone;

        PlaybackStatusText.Text = !statusKnown
            ? "Unavailable"
            : !hasWallpaper
                ? "None"
                : resolvedPaused
                    ? "Paused"
                    : "Running";

        PauseResumeWallpaperButton.Content = resolvedPaused ? "Resume" : "Pause";
        ActivePlaceholderIcon.Symbol = !hasWallpaper
            ? Symbol.SlideShow
            : isVideo
                ? Symbol.Video
                : Symbol.Pictures;

        _ = LoadWallpaperPreviewAsync(
            ActivePreviewImage,
            (isImage || isVideo) && !isMissingOnDisk ? activeWallpaperPath : null,
            isVideo: isVideo);
        UpdateLibraryActionState();
    }

    private void RefreshAutostartState()
    {
        isSyncingAutostart = true;
        try
        {
            AutostartCheckBox.IsChecked = autostartManager.IsEnabled;
        }
        finally
        {
            isSyncingAutostart = false;
        }
    }

    private void OnAutostartChecked(object sender, RoutedEventArgs args)
    {
        if (isSyncingAutostart) return;

        var success = autostartManager.Enable();
        if (!success)
        {
            StatusText.Text = "Failed to enable autostart in registry.";
            RefreshAutostartState();
        }
        else
        {
            StatusText.Text = "Autostart enabled.";
        }
    }

    private void OnAutostartUnchecked(object sender, RoutedEventArgs args)
    {
        if (isSyncingAutostart) return;

        var success = autostartManager.Disable();
        if (!success)
        {
            StatusText.Text = "Failed to disable autostart in registry.";
            RefreshAutostartState();
        }
        else
        {
            StatusText.Text = "Autostart disabled.";
        }
    }

    private async void OnRestoreLastWallpaperChanged(object sender, RoutedEventArgs args)
    {
        if (isSyncingAppSettings || closed) return;

        try
        {
            var current = await settingsService.LoadAsync();
            current.RestoreLastWallpaper = RestoreLastWallpaperCheckBox.IsChecked == true;
            var saved = await settingsService.SaveAsync(current);
            if (!closed)
            {
                StatusText.Text = saved
                    ? (current.RestoreLastWallpaper ? "Startup wallpaper restore enabled." : "Startup wallpaper restore disabled.")
                    : "Failed to save restore preference.";
            }
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Error saving RestoreLastWallpaper setting: {exception}");
            if (!closed)
            {
                StatusText.Text = "Failed to save restore preference.";
            }
        }
    }

    private async void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (isSyncingAppSettings || closed) return;
        if (ThemeComboBox.SelectedItem is not ComboBoxItem selectedItem) return;

        var tag = selectedItem.Tag as string;
        var normalizedTheme = AppSettings.NormalizeTheme(tag);
        ApplyThemePreference(normalizedTheme);

        try
        {
            var current = await settingsService.LoadAsync();
            current.Theme = normalizedTheme;
            await settingsService.SaveAsync(current);
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Error saving theme preference: {exception}");
        }
    }

    private void ApplyThemePreference(string? theme)
    {
        if (Navigation == null) return;

        Navigation.RequestedTheme = AppSettings.NormalizeTheme(theme) switch
        {
            AppSettings.ThemeLight => ElementTheme.Light,
            AppSettings.ThemeDark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    private void SelectThemeComboBoxItem(string? theme)
    {
        if (ThemeComboBox == null) return;
        var expectedTag = AppSettings.NormalizeTheme(theme) ?? AppSettings.ThemeSystem;

        for (var i = 0; i < ThemeComboBox.Items.Count; i++)
        {
            if (ThemeComboBox.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, expectedTag, StringComparison.OrdinalIgnoreCase))
            {
                ThemeComboBox.SelectedIndex = i;
                return;
            }
        }

        ThemeComboBox.SelectedIndex = 0;
    }

    private void RestoreAndShowWindow()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            AppWindow.Show(activateWindow: true);
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero) hwnd = (IntPtr)AppWindow.Id.Value;

            if (TrayInterop.IsIconic(hwnd))
            {
                TrayInterop.ShowWindow(hwnd, TrayInterop.SW_RESTORE);
            }
            else
            {
                TrayInterop.ShowWindow(hwnd, TrayInterop.SW_SHOW);
            }
            TrayInterop.SetForegroundWindow(hwnd);

            RefreshAutostartState();
            _ = LoadAppSettingsAsync();
            _ = LoadLibraryAsync();
            _ = SyncRuntimeStatusAsync();
        });
    }

    private void PerformAppExit()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            trayIcon.Dispose();
            App.ReleaseSingleInstance();
            closed = true;
            AppWindow.Closing -= OnClosing;
            Close();
            Application.Current.Exit();
            Environment.Exit(0);
        });
    }

    private void OnTrayOpenRequested()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            await controller.RequestOpenWindowAsync();
        });
    }

    private void OnTrayStopWallpaperRequested()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (IsInteractionBusy || closed) return;
            libraryBusy = true;
            UpdateLibraryActionState();
            try
            {
                var ensure = await runtimeClient.EnsureHostRunningAsync();
                if (closed) return;
                if (!ensure.Success)
                {
                    StatusText.Text = "Runtime unavailable.";
                    UpdateActiveDetails(statusKnown: false);
                    return;
                }

                var result = await controller.RequestStopWallpaperAsync();
                if (closed) return;

                if (result.Success)
                {
                    activeWallpaperPath = null;
                    activeWallpaperType = IpcConstants.WallpaperTypeNone;
                    activeWallpaperPaused = false;
                    activePlaybackState = IpcConstants.PlaybackStateNone;
                    UpdateActiveDetails();
                    RenderLibraryItems();
                    StatusText.Text = "Wallpaper stopped.";
                }
                else
                {
                    StatusText.Text = WallpaperLibraryController.MapIpcFailureStatusMessage(result.Message, "Runtime unavailable.");
                }
            }
            catch (Exception exception)
            {
                Trace.WriteLine(exception);
                if (!closed)
                {
                    StatusText.Text = "Runtime unavailable.";
                }
            }
            finally
            {
                libraryBusy = false;
                if (!closed)
                {
                    UpdateLibraryActionState();
                }
            }
        });
    }

    private void OnTrayExitRequested()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            await controller.RequestExitAsync();
        });
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (closed || args.WindowActivationState == WindowActivationState.Deactivated) return;
        RefreshAutostartState();
        _ = LoadLibraryAsync();
        _ = SyncRuntimeStatusAsync();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        controller.OnWindowCloseRequested(out var cancelClose);
        args.Cancel = cancelClose;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        closed = true;
        trayIcon.Dispose();
        App.ReleaseSingleInstance();
        Trace.WriteLine("Control window closed; runtime host continues in background.");
        if (diagnostics != null)
        {
            Trace.Listeners.Remove(diagnostics);
            diagnostics.Dispose();
        }
    }
}
