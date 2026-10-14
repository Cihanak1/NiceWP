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
using NiceWP.Infrastructure.Observatory;
using NiceWP.Infrastructure.Settings;
using NiceWP.Infrastructure.Tray;
using Windows.Storage;

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
    private readonly List<(string Id, string FilePath, string Type, string DisplayName, bool IsMissing, bool IsFavorite)> renderedLibrarySnapshot = [];
    private string? activeWallpaperPath;
    private string activeWallpaperType = IpcConstants.WallpaperTypeNone;
    private bool activeWallpaperPaused;
    private string activePlaybackState = IpcConstants.PlaybackStateNone;
    private bool closed;
    private bool selecting;
    private bool libraryBusy;
    private bool isRenameDialogOpen;
    private int libraryLoads;
    private bool isSyncingAutostart;
    private bool isSyncingAppSettings;
    private DispatcherTimer? performanceTimer;
    private bool isPerformancePageActive;
    private bool isRefreshingPerformance;
    private RuntimePerformanceStatus? latestPerformanceStatus;

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

    private bool IsInteractionBusy => libraryBusy || selecting || isRenameDialogOpen || libraryController.IsBusy;

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
                await libraryController.RecordLastUsedByPathAsync(validatedPath);
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
        var entry = GetEntryFromSender(sender);
        if (entry != null)
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
        var entry = GetEntryFromSender(sender);
        if (entry != null)
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

    private async void OnToggleFavorite(object sender, RoutedEventArgs args)
    {
        if (IsInteractionBusy || closed) return;
        var entry = GetEntryFromSender(sender);
        if (entry != null)
        {
            await libraryController.ToggleFavoriteAsync(entry.Id);
            if (closed) return;
            RenderLibraryItems();
        }
    }

    private static WallpaperLibraryEntryView? GetEntryFromSender(object? sender)
    {
        if (sender is FrameworkElement fe)
        {
            if (fe.DataContext is WallpaperLibraryEntryView entry) return entry;
            if (fe.Tag is WallpaperLibraryEntryView tagEntry) return tagEntry;
        }
        return null;
    }

    private async Task<PickFileResult?> PickRelinkFileAsync(bool isVideo)
    {
        var picker = new FileOpenPicker(AppWindow.Id);
        if (isVideo)
        {
            picker.FileTypeFilter.Add(".mp4");
        }
        else
        {
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
        }
        return await picker.PickSingleFileAsync();
    }

    private async void OnLocateWallpaper(object sender, RoutedEventArgs args)
    {
        if (IsInteractionBusy || closed) return;
        var entry = GetEntryFromSender(sender);
        if (entry == null) return;

        await LocateWallpaperCoreAsync(entry);
    }

    private async void OnMenuLocateFileClick(object sender, RoutedEventArgs args)
    {
        if (IsInteractionBusy || closed) return;
        var entry = GetEntryFromSender(sender);
        if (entry == null) return;

        await LocateWallpaperCoreAsync(entry);
    }

    private async Task LocateWallpaperCoreAsync(WallpaperLibraryEntryView entry)
    {
        libraryBusy = true;
        UpdateLibraryActionState();
        try
        {
            var file = await PickRelinkFileAsync(entry.IsVideo);
            if (file == null || closed) return;

            var result = await libraryController.RelinkAsync(entry.Id, file.Path);
            if (closed) return;

            if (result.Success)
            {
                foreach (var cardBorder in realizedCardBorders)
                {
                    if (cardBorder.DataContext is WallpaperLibraryEntryView bound &&
                        string.Equals(bound.Id, entry.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        if (cardBorder.FindName("CardPreviewImage") is Image previewImg)
                        {
                            previewImg.Tag = null;
                            previewImg.Source = null;
                            previewImg.Visibility = Visibility.Collapsed;
                        }
                        break;
                    }
                }
            }

            RenderLibraryItems();
            StatusText.Text = result.Success
                ? (result.Message ?? "File relinked.")
                : WallpaperLibraryController.MapUserStatusMessage(result.Error, result.Message);
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Failed to locate/relink wallpaper: {exception}");
            if (!closed)
            {
                StatusText.Text = "Failed to relink file.";
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

    private void OnMenuApplyClick(object sender, RoutedEventArgs args)
    {
        if (IsInteractionBusy || closed) return;
        OnApplyWallpaper(sender, args);
    }

    private void OnMenuFavoriteClick(object sender, RoutedEventArgs args)
    {
        if (IsInteractionBusy || closed) return;
        OnToggleFavorite(sender, args);
    }

    private void OnMenuRemoveClick(object sender, RoutedEventArgs args)
    {
        if (IsInteractionBusy || closed) return;
        OnRemoveWallpaper(sender, args);
    }

    private void OnMenuOpenLocationClick(object sender, RoutedEventArgs args)
    {
        if (IsInteractionBusy || closed) return;
        var entry = GetEntryFromSender(sender);
        if (entry == null) return;

        var result = libraryController.OpenFileLocation(entry.Id);
        if (!result.Success)
        {
            StatusText.Text = result.Message ?? "File not found.";
        }
    }

    private async void OnMenuRenameClick(object sender, RoutedEventArgs args)
    {
        if (IsInteractionBusy || isRenameDialogOpen || closed) return;
        var entry = GetEntryFromSender(sender);
        if (entry == null) return;

        var currentEntry = libraryController.Items.FirstOrDefault(item =>
            string.Equals(item.Id, entry.Id, StringComparison.OrdinalIgnoreCase)) ?? entry;

        isRenameDialogOpen = true;
        libraryBusy = true;
        UpdateLibraryActionState();
        try
        {
            var renameBox = new TextBox
            {
                Text = currentEntry.DisplayName,
                SelectionStart = 0,
                SelectionLength = currentEntry.DisplayName.Length,
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var errorText = new TextBlock
            {
                Text = "Name cannot be empty.",
                FontSize = 12,
                Foreground = Application.Current.Resources.TryGetValue("SystemFillColorCriticalBrush", out var critBrush) && critBrush is Brush cb
                    ? cb
                    : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 232, 17, 35)),
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 4, 0, 0)
            };

            renameBox.TextChanged += (_, _) =>
            {
                if (errorText.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(renameBox.Text))
                {
                    errorText.Visibility = Visibility.Collapsed;
                }
            };

            var contentPanel = new StackPanel { Spacing = 4 };
            contentPanel.Children.Add(new TextBlock
            {
                Text = "Enter a display name for this wallpaper:",
                Foreground = Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out var secBrush) && secBrush is Brush sb
                    ? sb
                    : null
            });
            contentPanel.Children.Add(renameBox);
            contentPanel.Children.Add(errorText);

            var dialog = new ContentDialog
            {
                XamlRoot = this.Content.XamlRoot,
                Title = "Rename Wallpaper",
                PrimaryButtonText = "Rename",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                Content = contentPanel
            };

            dialog.PrimaryButtonClick += (_, dArgs) =>
            {
                if (string.IsNullOrWhiteSpace(renameBox.Text))
                {
                    dArgs.Cancel = true;
                    errorText.Visibility = Visibility.Visible;
                }
            };

            var dialogResult = await dialog.ShowAsync();
            if (closed) return;

            if (dialogResult == ContentDialogResult.Primary)
            {
                var newName = renameBox.Text.Trim();
                var renameResult = await libraryController.RenameAsync(currentEntry.Id, newName);
                if (closed) return;

                RenderLibraryItems();
                StatusText.Text = renameResult.Success
                    ? (string.IsNullOrWhiteSpace(renameResult.Message) ? $"Renamed wallpaper: {newName}" : renameResult.Message)
                    : WallpaperLibraryController.MapUserStatusMessage(renameResult.Error, renameResult.Message);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Rename dialog failed: {ex}");
        }
        finally
        {
            isRenameDialogOpen = false;
            libraryBusy = false;
            if (!closed)
            {
                UpdateLibraryActionState();
            }
        }
    }

    private void OnCardMenuFlyoutOpening(object sender, object args)
    {
        if (sender is not MenuFlyout flyout || flyout.Target is not Border cardBorder || cardBorder.DataContext is not WallpaperLibraryEntryView entry)
        {
            return;
        }

        var currentEntry = libraryController.Items.FirstOrDefault(item =>
            string.Equals(item.Id, entry.Id, StringComparison.OrdinalIgnoreCase)) ?? entry;

        var isIdle = !IsInteractionBusy;

        foreach (var item in flyout.Items)
        {
            item.DataContext = currentEntry;
            item.Tag = currentEntry;

            if (item is MenuFlyoutItem mfi)
            {
                switch (mfi.Name)
                {
                    case "MenuApplyItem":
                        mfi.IsEnabled = currentEntry.CanApply && isIdle;
                        break;
                    case "MenuFavoriteItem":
                        mfi.IsEnabled = isIdle;
                        mfi.Text = currentEntry.IsFavorite ? "Unfavorite" : "Favorite";
                        if (mfi.FindName("MenuFavoriteIcon") is FontIcon favIcon)
                        {
                            favIcon.Glyph = currentEntry.IsFavorite ? "\uE735" : "\uE734";
                        }
                        break;
                    case "MenuRenameItem":
                        mfi.IsEnabled = isIdle;
                        break;
                    case "MenuOpenLocationItem":
                        mfi.IsEnabled = !currentEntry.IsMissing && isIdle;
                        break;
                    case "MenuLocateFileItem":
                        mfi.Visibility = currentEntry.IsMissing ? Visibility.Visible : Visibility.Collapsed;
                        mfi.IsEnabled = currentEntry.IsMissing && isIdle;
                        break;
                    case "MenuRemoveItem":
                        mfi.IsEnabled = currentEntry.CanRemove && isIdle;
                        break;
                }
            }
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs args)
    {
        if (libraryController == null || closed) return;
        libraryController.SetSearchText(LibrarySearchBox?.Text);
        RenderLibraryItems();
    }

    private void OnFilterSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (libraryController == null || closed) return;
        if (LibraryFilterComboBox?.SelectedItem is ComboBoxItem item &&
            item.Tag is string tagStr &&
            Enum.TryParse<WallpaperLibraryFilter>(tagStr, out var filter))
        {
            libraryController.SetFilter(filter);
            RenderLibraryItems();
        }
    }

    private void OnSortSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (libraryController == null || closed) return;
        if (LibrarySortComboBox?.SelectedItem is ComboBoxItem item &&
            item.Tag is string tagStr &&
            Enum.TryParse<WallpaperLibrarySort>(tagStr, out var sort))
        {
            libraryController.SetSort(sort);
            RenderLibraryItems();
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
        if (LibraryPage == null || ActivePage == null || PerformancePage == null || SettingsPage == null) return;
        var page = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        LibraryPage.Visibility = page == "Library" ? Visibility.Visible : Visibility.Collapsed;
        ActivePage.Visibility = page == "Active" ? Visibility.Visible : Visibility.Collapsed;
        PerformancePage.Visibility = page == "Performance" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;

        if (page == "Performance")
        {
            isPerformancePageActive = true;
            StartPerformancePolling();
        }
        else
        {
            isPerformancePageActive = false;
            StopPerformancePolling();
        }

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

        if (cardBorder.FindName("CardFavoriteButton") is Button favoriteButton)
        {
            var favoriteIcon = cardBorder.FindName("CardFavoriteIcon") as FontIcon;
            ConfigureFavoriteButton(favoriteButton, favoriteIcon, currentEntry);
        }

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
            applyButton.Visibility = currentEntry.IsMissing ? Visibility.Collapsed : Visibility.Visible;
            applyButton.IsEnabled = currentEntry.CanApply && isIdle;
        }

        if (cardBorder.FindName("CardLocateButton") is Button locateButton)
        {
            locateButton.Visibility = currentEntry.IsMissing ? Visibility.Visible : Visibility.Collapsed;
            locateButton.IsEnabled = isIdle;
        }

        if (cardBorder.FindName("CardRemoveButton") is Button removeButton)
        {
            removeButton.IsEnabled = currentEntry.CanRemove && isIdle;
        }
    }

    private void OnFavoriteButtonLoaded(object sender, RoutedEventArgs args) =>
        ConfigureFavoriteButtonFromSender(sender as Button);

    private void OnFavoriteButtonDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) =>
        ConfigureFavoriteButtonFromSender(sender as Button);

    private void ConfigureFavoriteButtonFromSender(Button? button)
    {
        if (button == null || button.DataContext is not WallpaperLibraryEntryView entry) return;
        var currentEntry = libraryController.Items.FirstOrDefault(item =>
            string.Equals(item.Id, entry.Id, StringComparison.OrdinalIgnoreCase)) ?? entry;
        var icon = button.FindName("CardFavoriteIcon") as FontIcon;
        ConfigureFavoriteButton(button, icon, currentEntry);
    }

    private void ConfigureFavoriteButton(Button? button, FontIcon? icon, WallpaperLibraryEntryView entry)
    {
        if (button == null) return;
        var isFav = entry.IsFavorite;
        if (icon != null)
        {
            icon.Glyph = isFav ? "\uE735" : "\uE734";
            if (isFav)
            {
                if (Application.Current.Resources.TryGetValue("AccentTextFillColorPrimaryBrush", out var brush) && brush is Brush accentBrush)
                {
                    icon.Foreground = accentBrush;
                }
            }
            else
            {
                if (Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out var brush) && brush is Brush secondaryBrush)
                {
                    icon.Foreground = secondaryBrush;
                }
            }
        }

        ToolTipService.SetToolTip(button, isFav ? "Remove from favorites" : "Add to favorites");
        AutomationProperties.SetName(button, isFav ? $"Remove {entry.DisplayName} from favorites" : $"Add {entry.DisplayName} to favorites");
        button.IsEnabled = !IsInteractionBusy;
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

        button.Visibility = currentEntry.IsMissing ? Visibility.Collapsed : Visibility.Visible;
        button.IsEnabled = currentEntry.CanApply && !IsInteractionBusy;
        AutomationProperties.SetName(button, $"Apply {currentEntry.DisplayName}");
    }

    private void OnLocateButtonLoaded(object sender, RoutedEventArgs args) =>
        ConfigureLocateButton(sender as Button);

    private void OnLocateButtonDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) =>
        ConfigureLocateButton(sender as Button);

    private void ConfigureLocateButton(Button? button)
    {
        if (button == null || button.DataContext is not WallpaperLibraryEntryView entry)
        {
            return;
        }

        var currentEntry = libraryController.Items.FirstOrDefault(item =>
            string.Equals(item.Id, entry.Id, StringComparison.OrdinalIgnoreCase)) ?? entry;

        button.Visibility = currentEntry.IsMissing ? Visibility.Visible : Visibility.Collapsed;
        button.IsEnabled = !IsInteractionBusy;
        AutomationProperties.SetName(button, $"Locate file for {currentEntry.DisplayName}");
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
                current.IsMissing != previous.IsMissing ||
                current.IsFavorite != previous.IsFavorite)
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
            renderedLibrarySnapshot.Add((item.Id, item.FilePath, item.Type, item.DisplayName, item.IsMissing, item.IsFavorite));
        }
    }

    private void RenderLibraryItems()
    {
        if (closed) return;

        var items = libraryController.Items;
        var totalCount = libraryController.AllItems.Count;
        var count = items.Count;
        var missingCount = 0;
        for (var i = 0; i < count; i++)
        {
            if (items[i].IsMissing)
            {
                missingCount++;
            }
        }

        var isFilteredOrSearched = libraryController.Filter != WallpaperLibraryFilter.All ||
            !string.IsNullOrWhiteSpace(libraryController.SearchText);

        if (totalCount == 0)
        {
            LibraryCountText.Text = "0 wallpapers in collection";
            EmptyTitleText.Text = "No wallpapers in library";
            EmptySubtitleText.Text = "Use Add Wallpaper to add an image or MP4.";
        }
        else if (isFilteredOrSearched)
        {
            LibraryCountText.Text = count switch
            {
                0 => $"0 of {totalCount} wallpapers found",
                1 => missingCount > 0 ? $"1 of {totalCount} wallpapers found (1 missing on disk)" : $"1 of {totalCount} wallpapers found",
                _ => missingCount > 0 ? $"{count} of {totalCount} wallpapers found ({missingCount} missing on disk)" : $"{count} of {totalCount} wallpapers found"
            };
            EmptyTitleText.Text = "No matching wallpapers";
            EmptySubtitleText.Text = "Try adjusting your search or filter.";
        }
        else
        {
            LibraryCountText.Text = count switch
            {
                0 => "0 wallpapers in collection",
                1 => missingCount > 0 ? "1 wallpaper in collection (1 missing on disk)" : "1 wallpaper in collection",
                _ => missingCount > 0 ? $"{count} wallpapers in collection ({missingCount} missing on disk)" : $"{count} wallpapers in collection"
            };
            EmptyTitleText.Text = "No wallpapers in library";
            EmptySubtitleText.Text = "Use Add Wallpaper to add an image or MP4.";
        }

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
        LibrarySearchBox.IsEnabled = isIdle;
        LibraryFilterComboBox.IsEnabled = isIdle;
        LibrarySortComboBox.IsEnabled = isIdle;
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

                if (cardBorder.FindName("CardFavoriteButton") is Button favoriteButton)
                {
                    favoriteButton.IsEnabled = isIdle;
                }

                if (cardBorder.FindName("CardApplyButton") is Button applyButton)
                {
                    applyButton.Visibility = currentEntry.IsMissing ? Visibility.Collapsed : Visibility.Visible;
                    applyButton.IsEnabled = currentEntry.CanApply && isIdle;
                }

                if (cardBorder.FindName("CardLocateButton") is Button locateButton)
                {
                    locateButton.Visibility = currentEntry.IsMissing ? Visibility.Visible : Visibility.Collapsed;
                    locateButton.IsEnabled = isIdle;
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
            if (isPerformancePageActive)
            {
                StartPerformancePolling();
            }
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
        if (closed) return;

        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            StopPerformancePolling();
            return;
        }

        if (isPerformancePageActive)
        {
            StartPerformancePolling();
        }

        RefreshAutostartState();
        _ = LoadLibraryAsync();
        _ = SyncRuntimeStatusAsync();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        StopPerformancePolling();
        controller.OnWindowCloseRequested(out var cancelClose);
        args.Cancel = cancelClose;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        StopPerformancePolling();
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

    private void StartPerformancePolling()
    {
        if (closed) return;

        if (performanceTimer == null)
        {
            performanceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            performanceTimer.Tick += OnPerformanceTimerTick;
        }

        if (!performanceTimer.IsEnabled)
        {
            performanceTimer.Start();
        }

        _ = RefreshPerformanceAsync();
    }

    private void StopPerformancePolling()
    {
        if (performanceTimer != null && performanceTimer.IsEnabled)
        {
            performanceTimer.Stop();
        }
    }

    private void OnPerformanceTimerTick(object? sender, object e)
    {
        if (!isPerformancePageActive || closed)
        {
            StopPerformancePolling();
            return;
        }

        _ = RefreshPerformanceAsync();
    }

    private async Task RefreshPerformanceAsync()
    {
        if (closed || isRefreshingPerformance) return;
        isRefreshingPerformance = true;

        try
        {
            var response = await runtimeClient.GetPerformanceStatusAsync(timeoutMs: 2000);
            if (closed || !isPerformancePageActive) return;

            if (response.Success && response.Performance != null)
            {
                latestPerformanceStatus = response.Performance;
                RenderPerformanceStatus(response.Performance);
            }
            else
            {
                RenderPerformanceUnavailable(response.Message ?? "Runtime host unreachable.");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error querying performance telemetry: {ex.Message}");
            if (!closed && isPerformancePageActive)
            {
                RenderPerformanceUnavailable("Telemetry capture failed.");
            }
        }
        finally
        {
            isRefreshingPerformance = false;
        }
    }

    private void RenderPerformanceStatus(RuntimePerformanceStatus status)
    {
        var snap = status.Snapshot;
        var stats = status.Stats;

        PerformanceHostStatusTitle.Text = "Runtime Host: Connected";
        PerformanceHostStatusDetails.Text = $"PID: {snap.ProcessId} • Uptime: {FormatTimeSpan(snap.Uptime)}";
        PerformanceLiveIndicator.Visibility = Visibility.Visible;

        PerfCpuCurrentText.Text = stats.CurrentCpuPercent.HasValue ? $"{stats.CurrentCpuPercent.Value:F1}%" : "--%";
        PerfWorkingSetText.Text = FormatMegabytes(snap.WorkingSetBytes);
        PerfPrivateMemoryText.Text = FormatMegabytes(snap.PrivateMemoryBytes);
        PerfThreadsText.Text = snap.ThreadCount.ToString();
        PerfHandlesText.Text = snap.HandleCount.ToString();
        PerfUptimeText.Text = FormatTimeSpan(snap.Uptime);

        PerfSampleCountText.Text = $"{stats.SampleCount} samples (~{stats.SampleCount}s)";
        var cpuAvg = stats.AverageCpuPercent.HasValue ? $"{stats.AverageCpuPercent.Value:F1}%" : "--%";
        var cpuPeak = stats.PeakCpuPercent.HasValue ? $"{stats.PeakCpuPercent.Value:F1}%" : "--%";
        PerfCpuStatsText.Text = $"Avg: {cpuAvg} • Peak: {cpuPeak}";

        var ramAvg = FormatMegabytes(stats.AverageWorkingSetBytes);
        var ramPeak = FormatMegabytes(stats.PeakWorkingSetBytes);
        PerfRamStatsText.Text = $"Avg: {ramAvg} • Peak: {ramPeak}";

        var privAvg = FormatMegabytes(stats.AveragePrivateMemoryBytes);
        var privPeak = FormatMegabytes(stats.PeakPrivateMemoryBytes);
        PerfPrivateStatsText.Text = $"Avg: {privAvg} • Peak: {privPeak}";

        PerfThreadPeakText.Text = $"Peak: {stats.PeakThreadCount}";
        PerfHandlePeakText.Text = $"Peak: {stats.PeakHandleCount}";

        PerfWallpaperTypeStateText.Text = $"{snap.WallpaperType} • {snap.WallpaperState}";
        PerfRendererStatusText.Text = snap.RendererWindowAlive ? "Alive (HWND valid)" : "Inactive";

        if (snap.IsImageActive)
        {
            PerfSourceResolutionText.Text = FormatDimensions(snap.SourceWidth, snap.SourceHeight);
            var decRes = FormatDimensions(snap.DecodeWidth, snap.DecodeHeight);
            var decMem = snap.ApproximateDecodedMemoryBytes.HasValue ? $" ({FormatMegabytes(snap.ApproximateDecodedMemoryBytes.Value)})" : "";
            PerfDecodeResolutionText.Text = $"{decRes}{decMem}";
            PerfMediaDetailsText.Text = $"Path: {snap.ActiveWallpaperPath ?? "(None)"}";
        }
        else if (snap.IsVideoActive)
        {
            PerfSourceResolutionText.Text = FormatDimensions(snap.VideoWidth, snap.VideoHeight);
            PerfDecodeResolutionText.Text = FormatDimensions(snap.DecodeWidth, snap.DecodeHeight);
            var pos = FormatTimeSpan(snap.PlaybackPosition);
            var dur = FormatTimeSpan(snap.PlaybackDuration);
            PerfMediaDetailsText.Text = $"Status: {snap.PlaybackStatus ?? "Unknown"} • Pos: {pos} / {dur} • Path: {snap.ActiveWallpaperPath ?? "(None)"}";
        }
        else
        {
            PerfSourceResolutionText.Text = "None";
            PerfDecodeResolutionText.Text = "None";
            PerfMediaDetailsText.Text = "No active wallpaper.";
        }

        PerfDesktopHostStateText.Text = snap.DesktopHostConnected ? "Connected (WorkerW valid)" : "Disconnected";
        PerfPauseReasonsText.Text = snap.PauseReasons;
        PerfFullscreenPolicyText.Text = snap.FullscreenPauseActive ? "Active (Auto-paused by fullscreen window)" : "Inactive (Desktop visible)";
        PerfPowerPolicyText.Text = snap.PowerPauseActive ? "Active (Auto-paused by power policy)" : "Inactive (AC or policy satisfied)";

        if (snap.GpuPercent.HasValue)
        {
            PerfGpuStatusText.Text = $"GPU Utilization: {snap.GpuPercent.Value:F1}% • Dedicated Memory: {FormatMegabytes(snap.GpuMemoryBytes ?? 0)}";
        }
        else
        {
            PerfGpuStatusText.Text = snap.GpuStatusMessage ?? "GPU metrics: Unavailable. Windows process-level GPU counters require full D3D engine enumeration across all processes, adding 2+ seconds overhead per sample.";
        }
    }

    private void RenderPerformanceUnavailable(string reason)
    {
        PerformanceHostStatusTitle.Text = "Runtime Host: Unavailable";
        PerformanceHostStatusDetails.Text = reason;
        PerformanceLiveIndicator.Visibility = Visibility.Collapsed;

        PerfCpuCurrentText.Text = "--%";
        PerfWorkingSetText.Text = "-- MB";
        PerfPrivateMemoryText.Text = "-- MB";
        PerfThreadsText.Text = "--";
        PerfHandlesText.Text = "--";
        PerfUptimeText.Text = "--";

        PerfSampleCountText.Text = "0 samples";
        PerfCpuStatsText.Text = "Avg: --% • Peak: --%";
        PerfRamStatsText.Text = "Avg: -- MB • Peak: -- MB";
        PerfPrivateStatsText.Text = "Avg: -- MB • Peak: -- MB";
        PerfThreadPeakText.Text = "Peak: --";
        PerfHandlePeakText.Text = "Peak: --";

        PerfWallpaperTypeStateText.Text = "Unknown";
        PerfRendererStatusText.Text = "Inactive";
        PerfSourceResolutionText.Text = "Unavailable";
        PerfDecodeResolutionText.Text = "Unavailable";
        PerfMediaDetailsText.Text = "Runtime host is not responding.";

        PerfDesktopHostStateText.Text = "Disconnected";
        PerfPauseReasonsText.Text = "Unknown";
        PerfFullscreenPolicyText.Text = "Unavailable";
        PerfPowerPolicyText.Text = "Unavailable";
    }

    private async void OnExportReportClicked(object sender, RoutedEventArgs args)
    {
        if (closed) return;

        try
        {
            var picker = new FileSavePicker(AppWindow.Id);
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeChoices.Add("Plain Text (*.txt)", new List<string> { ".txt" });
            picker.SuggestedFileName = $"NiceWP-Performance-Report-{DateTime.Now:yyyyMMdd-HHmmss}.txt";

            var file = await picker.PickSaveFileAsync();
            if (file != null && !string.IsNullOrWhiteSpace(file.Path))
            {
                var status = latestPerformanceStatus;
                if (status == null)
                {
                    var response = await runtimeClient.GetPerformanceStatusAsync(timeoutMs: 1500);
                    if (response.Success && response.Performance != null)
                    {
                        status = response.Performance;
                    }
                }

                var reportText = PerformanceReportGenerator.GenerateTextReport(status ?? new RuntimePerformanceStatus());
                await File.WriteAllTextAsync(file.Path, reportText);
                StatusText.Text = $"Performance report exported to {Path.GetFileName(file.Path)}";
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error exporting performance report: {ex.Message}");
            StatusText.Text = "Failed to export performance report.";
        }
    }

    private static string FormatMegabytes(long bytes) =>
        $"{bytes / (1024.0 * 1024.0):F1} MB";

    private static string FormatTimeSpan(TimeSpan? ts) =>
        ts.HasValue ? $"{(int)ts.Value.TotalHours:D2}:{ts.Value.Minutes:D2}:{ts.Value.Seconds:D2}" : "--:--:--";

    private static string FormatDimensions(uint? w, uint? h) =>
        w.HasValue && h.HasValue ? $"{w.Value} x {h.Value}" : "Unavailable";
}
