using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
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
using NiceWP.Infrastructure.Remote;
using NiceWP.Infrastructure.Rotation;
using NiceWP.Infrastructure.Localization;
using Windows.ApplicationModel.DataTransfer;
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
    private DispatcherTimer? rotationCountdownTimer;
    private bool isPerformancePageActive;
    private bool isRefreshingPerformance;
    private RuntimePerformanceStatus? latestPerformanceStatus;
    private bool libraryRenderDeferred;
    private bool isUpdatingFeedbackText;

    public DesktopTestWindow(bool isStartup = false)
    {
        libraryRenderDeferred = isStartup;
        InitializeComponent();
        LocalizationService.Instance.LanguageChanged += OnLocalizationLanguageChanged;
        UpdateNavigationLocalization();
        UpdateLibraryLocalization();
        UpdateActiveWallpaperLocalization();
        UpdateSettingsLocalization();
        UpdatePerformanceLocalization();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Brand", "nicewp-app.ico"));
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
        libraryController.Rotation.WallpaperRotated += OnRotationWallpaperRotated;
        libraryController.Rotation.StatusChanged += OnRotationStatusChanged;

        controller = new TrayLifecycleController(
            runtimeClient.IpcClient,
            initialWindowVisible: !isStartup,
            restoreWindowAction: RestoreAndShowWindow,
            hideWindowAction: () =>
            {
                StopPerformancePolling();
                StopRotationCountdownTimer();
                AppWindow.Hide();
            },
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
                SelectEfficiencyProfileComboBoxItem(settings.EfficiencyProfile);
                SelectRotationModeComboBoxItem(settings.RotationMode);
                SelectRotationOrderComboBoxItem(settings.RotationOrder);
                SelectRotationIntervalComboBoxItem(settings.RotationIntervalMinutes);
                UpdateRotationSettingsStatus(settings.RotationMode, settings.RotationOrder, settings.RotationIntervalMinutes);
            }
            finally
            {
                isSyncingAppSettings = false;
            }

            await libraryController.Rotation.InitializeAsync(settings);
            UpdateRotationDetailsUI();
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

    private async Task<string?> PickWallpaperFolderAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero)
        {
            hwnd = (IntPtr)AppWindow.Id.Value;
        }
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
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

    private async void OnAddFolderClicked(object sender, RoutedEventArgs args)
    {
        if (IsInteractionBusy || closed) return;
        libraryBusy = true;
        UpdateLibraryActionState();
        try
        {
            var folderPath = await PickWallpaperFolderAsync();
            if (string.IsNullOrWhiteSpace(folderPath) || closed) return;

            var result = await libraryController.AddFolderAsync(folderPath, recursive: false);
            if (closed) return;

            RenderLibraryItems();
            StatusText.Text = result.Message ?? string.Empty;
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

    private void OnToggleSelectModeClicked(object sender, RoutedEventArgs args)
    {
        if (closed) return;
        if (libraryController.IsSelectionMode)
        {
            libraryController.ExitSelectionMode();
        }
        else
        {
            libraryController.EnterSelectionMode();
        }
        UpdateMultiSelectUI();
        RenderLibraryItems();
    }

    private void OnBulkSelectAllClicked(object sender, RoutedEventArgs args)
    {
        if (closed) return;
        libraryController.SelectAllVisible();
        UpdateMultiSelectUI();
    }

    private void OnBulkClearSelectionClicked(object sender, RoutedEventArgs args)
    {
        if (closed) return;
        libraryController.ClearSelection();
        UpdateMultiSelectUI();
    }

    private async void OnBulkFavoriteClicked(object sender, RoutedEventArgs args)
    {
        if (closed || libraryController.SelectedCount == 0 || IsInteractionBusy) return;
        libraryBusy = true;
        UpdateLibraryActionState();
        try
        {
            var result = await libraryController.BulkSetFavoriteSelectedAsync(true);
            RenderLibraryItems();
            UpdateMultiSelectUI();
            StatusText.Text = result.Message ?? string.Empty;
        }
        finally
        {
            libraryBusy = false;
            UpdateLibraryActionState();
        }
    }

    private async void OnBulkUnfavoriteClicked(object sender, RoutedEventArgs args)
    {
        if (closed || libraryController.SelectedCount == 0 || IsInteractionBusy) return;
        libraryBusy = true;
        UpdateLibraryActionState();
        try
        {
            var result = await libraryController.BulkSetFavoriteSelectedAsync(false);
            RenderLibraryItems();
            UpdateMultiSelectUI();
            StatusText.Text = result.Message ?? string.Empty;
        }
        finally
        {
            libraryBusy = false;
            UpdateLibraryActionState();
        }
    }

    private async void OnBulkRemoveClicked(object sender, RoutedEventArgs args)
    {
        if (closed || libraryController.SelectedCount == 0 || IsInteractionBusy) return;

        var count = libraryController.SelectedCount;
        var isTr = LocalizationService.Instance.CurrentLanguage == EffectiveLanguage.Turkish;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = isTr ? $"{count} duvar kağıdını kaldır?" : $"Remove {count} {(count == 1 ? "wallpaper" : "wallpapers")}?",
            Content = isTr
                ? $"{count} duvar kağıdı NiceWP'den kaldırılacak. Fiziksel dosyalarınız diskten silinmez."
                : $"This will remove {count} {(count == 1 ? "wallpaper" : "wallpapers")} from NiceWP. Your physical files will not be deleted from disk.",
            PrimaryButtonText = isTr ? "Kaldır" : "Remove",
            CloseButtonText = isTr ? "İptal" : "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        var dialogResult = await dialog.ShowAsync();
        if (dialogResult != ContentDialogResult.Primary) return;

        libraryBusy = true;
        UpdateLibraryActionState();
        try
        {
            var result = await libraryController.BulkRemoveSelectedAsync();
            RenderLibraryItems();
            UpdateMultiSelectUI();
            StatusText.Text = result.Message ?? string.Empty;
        }
        finally
        {
            libraryBusy = false;
            UpdateLibraryActionState();
        }
    }

    private void OnBulkDoneClicked(object sender, RoutedEventArgs args)
    {
        if (closed) return;
        libraryController.ExitSelectionMode();
        UpdateMultiSelectUI();
        RenderLibraryItems();
    }

    private void OnCardSelectCheckBoxLoaded(object sender, RoutedEventArgs args) =>
        ConfigureCardSelectCheckBox(sender as CheckBox);

    private void OnCardSelectCheckBoxDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) =>
        ConfigureCardSelectCheckBox(sender as CheckBox);

    private void ConfigureCardSelectCheckBox(CheckBox? checkBox)
    {
        if (checkBox == null || checkBox.DataContext is not WallpaperLibraryEntryView entry) return;
        checkBox.Visibility = libraryController.IsSelectionMode ? Visibility.Visible : Visibility.Collapsed;
        checkBox.IsChecked = libraryController.IsItemSelected(entry.Id);
        AutomationProperties.SetName(checkBox, $"Select {entry.DisplayName}");
    }

    private void OnCardSelectCheckBoxClicked(object sender, RoutedEventArgs args)
    {
        if (sender is CheckBox cb && cb.DataContext is WallpaperLibraryEntryView entry)
        {
            libraryController.SelectItemForMultiSelect(entry.Id, cb.IsChecked == true);
            UpdateMultiSelectUI();
        }
    }

    private void OnLibraryDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add to NiceWP library";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsContentVisible = true;
            DropOverlayBorder.Visibility = Visibility.Visible;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private void OnLibraryDragLeave(object sender, DragEventArgs e)
    {
        DropOverlayBorder.Visibility = Visibility.Collapsed;
    }

    private async void OnLibraryDrop(object sender, DragEventArgs e)
    {
        DropOverlayBorder.Visibility = Visibility.Collapsed;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        var items = await e.DataView.GetStorageItemsAsync();
        if (items == null || items.Count == 0 || closed) return;

        var paths = new List<string>();
        foreach (var item in items)
        {
            if (item is Windows.Storage.StorageFile file)
            {
                paths.Add(file.Path);
            }
            else if (item is Windows.Storage.StorageFolder folder)
            {
                var scanned = WallpaperFolderScanner.Scan(folder.Path, recursive: false);
                paths.AddRange(scanned);
            }
        }

        if (paths.Count > 0)
        {
            libraryBusy = true;
            UpdateLibraryActionState();
            try
            {
                var result = await libraryController.AddFilesBatchAsync(paths);
                if (closed) return;
                RenderLibraryItems();
                StatusText.Text = result.Message ?? string.Empty;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Error adding dropped wallpapers: {ex.Message}");
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
    }

    private async void OnAddFromUrlClicked(object sender, RoutedEventArgs args)
    {
        if (IsInteractionBusy || closed) return;

        var contentPanel = (StackPanel)((DataTemplate)Navigation.Resources["RemoteImportTemplate"]).LoadContent();
        var urlBox = (TextBox)contentPanel.FindName("RemoteUrlBox");
        var applyCheckBox = (CheckBox)contentPanel.FindName("RemoteApplyCheckBox");
        var progressBar = (ProgressBar)contentPanel.FindName("RemoteProgressBar");
        var statusBlock = (TextBlock)contentPanel.FindName("RemoteStatusText");
        var errorBlock = (TextBlock)contentPanel.FindName("RemoteErrorText");
        errorBlock.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) =>
        {
            var hasError = errorBlock.Visibility == Visibility.Visible;
            statusBlock.Visibility = hasError ? Visibility.Collapsed : Visibility.Visible;
            if (hasError)
            {
                statusBlock.Text = string.Empty;
                progressBar.Visibility = Visibility.Collapsed;
            }
        });

        urlBox.TextChanged += (_, _) =>
        {
            if (errorBlock.Visibility == Visibility.Visible)
            {
                errorBlock.Visibility = Visibility.Collapsed;
            }
        };

        var isTrRemote = LocalizationService.Instance.CurrentLanguage == EffectiveLanguage.Turkish;
        var dialog = new ContentDialog
        {
            XamlRoot = this.Content.XamlRoot,
            Title = isTrRemote ? "Uzak Duvar Kağıdı İndir" : "Import Remote Wallpaper",
            RequestedTheme = Navigation.RequestedTheme,
            PrimaryButtonText = isTrRemote ? "İndir" : "Download",
            CloseButtonText = isTrRemote ? "İptal" : "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = contentPanel
        };

        CancellationTokenSource? downloadCts = null;
        var isDownloading = false;

        dialog.PrimaryButtonClick += async (_, dArgs) =>
        {
            dArgs.Cancel = true;

            if (isDownloading) return;

            var rawUrl = urlBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(rawUrl))
            {
                errorBlock.Text = "Invalid URL.";
                errorBlock.Visibility = Visibility.Visible;
                return;
            }

            errorBlock.Visibility = Visibility.Collapsed;
            urlBox.IsEnabled = false;
            applyCheckBox.IsEnabled = false;
            dialog.IsPrimaryButtonEnabled = false;
            progressBar.Visibility = Visibility.Visible;
            progressBar.IsIndeterminate = true;
            statusBlock.Text = "Connecting...";

            isDownloading = true;
            downloadCts = new CancellationTokenSource();

            var progress = new Progress<RemoteDownloadProgress>(p =>
            {
                if (closed) return;
                if (!isDownloading || errorBlock.Visibility == Visibility.Visible) return;
                statusBlock.Text = p.DisplayText;
                if (p.Percentage.HasValue)
                {
                    progressBar.IsIndeterminate = false;
                    progressBar.Value = p.Percentage.Value;
                }
                else
                {
                    progressBar.IsIndeterminate = true;
                }
            });

            try
            {
                var shouldApply = applyCheckBox.IsChecked == true;
                var result = await libraryController.ImportFromUrlAsync(rawUrl, shouldApply, progress, downloadCts.Token);
                if (closed) return;

                if (result.Success)
                {
                    dialog.Hide();
                    RenderLibraryItems();
                    if (shouldApply && result.Entry != null)
                    {
                        await SyncRuntimeStatusAsync();
                    }
                    StatusText.Text = result.Message ?? "Added to library.";
                }
                else if (result.Message == "Download cancelled.")
                {
                    dialog.Hide();
                    StatusText.Text = "Download cancelled.";
                }
                else
                {
                    progressBar.Visibility = Visibility.Collapsed;
                    urlBox.IsEnabled = true;
                    applyCheckBox.IsEnabled = true;
                    dialog.IsPrimaryButtonEnabled = true;
                    errorBlock.Text = result.Message ?? "Could not connect to server.";
                    errorBlock.Visibility = Visibility.Visible;
                    isDownloading = false;
                }
            }
            catch (OperationCanceledException)
            {
                if (!closed)
                {
                    dialog.Hide();
                    StatusText.Text = "Download cancelled.";
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Remote import UI error: {ex}");
                if (!closed)
                {
                    progressBar.Visibility = Visibility.Collapsed;
                    urlBox.IsEnabled = true;
                    applyCheckBox.IsEnabled = true;
                    dialog.IsPrimaryButtonEnabled = true;
                    errorBlock.Text = "Could not connect to server.";
                    errorBlock.Visibility = Visibility.Visible;
                    isDownloading = false;
                }
            }
        };

        dialog.CloseButtonClick += (_, _) =>
        {
            if (isDownloading && downloadCts != null)
            {
                try { downloadCts.Cancel(); } catch { }
            }
        };

        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to show Add from URL dialog: {ex}");
        }
        finally
        {
            downloadCts?.Dispose();
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

            var isTrRename = LocalizationService.Instance.CurrentLanguage == EffectiveLanguage.Turkish;
            var dialog = new ContentDialog
            {
                XamlRoot = this.Content.XamlRoot,
                Title = isTrRename ? "Duvar Kağıdını Yeniden Adlandır" : "Rename Wallpaper",
                PrimaryButtonText = isTrRename ? "Yeniden Adlandır" : "Rename",
                CloseButtonText = isTrRename ? "İptal" : "Cancel",
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
                        mfi.Text = LocalizationService.Instance.GetString(LocalizationKeys.ItemApply);
                        mfi.IsEnabled = currentEntry.CanApply && isIdle;
                        break;
                    case "MenuFavoriteItem":
                        mfi.IsEnabled = isIdle;
                        mfi.Text = currentEntry.IsFavorite ? LocalizationService.Instance.GetString(LocalizationKeys.LibUnfavorite) : LocalizationService.Instance.GetString(LocalizationKeys.LibFavorite);
                        if (mfi.FindName("MenuFavoriteIcon") is FontIcon favIcon)
                        {
                            favIcon.Glyph = currentEntry.IsFavorite ? "\uE735" : "\uE734";
                        }
                        break;
                    case "MenuRenameItem":
                        mfi.Text = LocalizationService.Instance.GetString(LocalizationKeys.ItemRename);
                        mfi.IsEnabled = isIdle;
                        break;
                    case "MenuOpenLocationItem":
                        mfi.Text = LocalizationService.Instance.GetString(LocalizationKeys.ItemOpenFileLocation);
                        mfi.IsEnabled = !currentEntry.IsMissing && isIdle;
                        break;
                    case "MenuLocateFileItem":
                        mfi.Text = LocalizationService.Instance.GetString(LocalizationKeys.ItemLocateFile);
                        mfi.Visibility = currentEntry.IsMissing ? Visibility.Visible : Visibility.Collapsed;
                        mfi.IsEnabled = currentEntry.IsMissing && isIdle;
                        break;
                    case "MenuRemoveItem":
                        mfi.Text = LocalizationService.Instance.GetString(LocalizationKeys.LibRemove);
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

        if (page == "Active")
        {
            StartRotationCountdownTimer();
            UpdateRotationDetailsUI();
            AdjustActiveLayout();
            if (controller != null)
            {
                await SyncRuntimeStatusAsync();
            }
        }
        else
        {
            StopRotationCountdownTimer();
            if (page == "Settings")
            {
                RefreshAutostartState();
                await LoadAppSettingsAsync();
            }
        }
    }

    private void OnGalleryLoaded(object sender, RoutedEventArgs args) => ResizeGallery();

    private void OnGallerySizeChanged(object sender, SizeChangedEventArgs args) => ResizeGallery();

    private void OnResponsivePanelSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is VariableSizedWrapGrid panel)
        {
            var width = Math.Max(1, args.NewSize.Width);
            var preferredWidth = panel == LibraryControlStrip ? 160 : 240;
            var columns = Math.Max(1, (int)(width / preferredWidth));
            panel.ItemWidth = Math.Floor(width / columns);
            if (panel == LibraryControlStrip)
                VariableSizedWrapGrid.SetColumnSpan(LibrarySearchBox, columns > 1 ? 2 : 1);
        }
    }

    private void OnActiveLayoutSizeChanged(object sender, SizeChangedEventArgs args)
    {
        AdjustActiveLayout();
    }

    private void AdjustActiveLayout()
    {
        if (ActivePage == null || ActivePreviewFrame == null || ActivePage.ActualHeight <= 0) return;
        var availableWidth = ActiveContentPanel?.ActualWidth > 0 ? ActiveContentPanel.ActualWidth : ActivePage.ActualWidth;
        if (availableWidth <= 0) return;

        // Keep controls first; scale a 16:9 preview to the actual scroll viewport.
        var maxHeight = Math.Clamp(ActivePage.ActualHeight * 0.4, 150, 360);
        var targetWidth = Math.Round(Math.Min(availableWidth, maxHeight * 16.0 / 9.0));
        var targetHeight = Math.Round(targetWidth * 9.0 / 16.0);

        if (Math.Abs(ActivePreviewFrame.Width - targetWidth) > 1.0 ||
            Math.Abs(ActivePreviewFrame.Height - targetHeight) > 1.0)
        {
            ActivePreviewFrame.Width = targetWidth;
            ActivePreviewFrame.Height = targetHeight;
        }
    }

    private void OnCardPreviewSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is Grid preview && args.NewSize.Width > 0)
            preview.Height = args.NewSize.Width * 9 / 16;
    }

    private void ResizeGallery()
    {
        if (LibraryListView.ItemsPanelRoot is ItemsWrapGrid panel)
        {
            var width = Math.Max(1, LibraryListView.ActualWidth - 16);
            var columns = Math.Max(1, (int)Math.Ceiling(width / 416));
            if (columns > 1 && width / columns < 320) columns--;
            panel.ItemWidth = Math.Floor(width / columns);
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
                badgeText.Text = isActive ? "ACTIVE · MISSING" : "MISSING";
            }
            else if (isActive)
            {
                badge.Visibility = Visibility.Visible;
                badgeText.Text = "ACTIVE";
            }
            else if (currentEntry.IsRemoteImported)
            {
                badge.Visibility = Visibility.Visible;
                badgeText.Text = "REMOTE · LOCAL COPY";
            }
            else
            {
                badge.Visibility = Visibility.Collapsed;
            }
            badgeText.Style = (Style)Application.Current.Resources[
                isActive ? "ActiveChipTextStyle" : "NeutralChipTextStyle"];
            ToolTipService.SetToolTip(badge, currentEntry.IsRemoteImported && !currentEntry.IsMissing
                ? "Downloaded and validated locally." : badgeText.Text);
        }

        ToolTipService.SetToolTip(cardBorder, currentEntry.IsRemoteImported
            ? $"{currentEntry.DisplayName}\nREMOTE · LOCAL COPY\nDownloaded and validated locally."
            : currentEntry.ToolTipText);
        var isIdle = !IsInteractionBusy;
        if (cardBorder.FindName("CardApplyButton") is Button applyButton)
        {
            applyButton.Visibility = currentEntry.IsMissing ? Visibility.Collapsed : Visibility.Visible;
            applyButton.IsEnabled = currentEntry.CanApply && isIdle;
            applyButton.Content = isActive ? LocalizationService.Instance.GetString(LocalizationKeys.ItemReapply) : LocalizationService.Instance.GetString(LocalizationKeys.ItemApply);
        }

        if (cardBorder.FindName("CardLocateButton") is Button locateButton)
        {
            locateButton.Visibility = currentEntry.IsMissing ? Visibility.Visible : Visibility.Collapsed;
            locateButton.Content = LocalizationService.Instance.GetString(LocalizationKeys.ItemLocateFile);
            locateButton.IsEnabled = isIdle;
        }

        if (cardBorder.FindName("CardRemoveButton") is Button removeButton)
        {
            removeButton.Content = LocalizationService.Instance.GetString(LocalizationKeys.LibRemove);
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
        if (closed || libraryRenderDeferred) return;

        var items = libraryController.Items;
        var totalCount = libraryController.AllItems.Count;
        var count = items.Count;
        var favoriteCount = libraryController.AllItems.Count(item => item.IsFavorite);
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

        if (libraryController.IsSelectionMode)
        {
            var selected = libraryController.SelectedCount;
            LibraryCountText.Text = LocalizationService.Instance.Format(LocalizationKeys.ItemSelectedCount, selected);
        }
        else if (totalCount == 0)
        {
            LibraryCountText.Text = "0 wallpapers in collection";
            EmptyTitleText.Text = "No wallpapers in library";
            EmptySubtitleText.Text = "Use Add Wallpaper, Add Folder, or drag & drop files here.";
        }
        else if (isFilteredOrSearched)
        {
            var parts = new List<string>(3)
            {
                $"Showing {count} of {totalCount} wallpapers"
            };
            if (favoriteCount > 0)
            {
                parts.Add($"{favoriteCount} favorites");
            }
            if (missingCount > 0)
            {
                parts.Add($"{missingCount} missing on disk");
            }

            LibraryCountText.Text = string.Join(" • ", parts);
            EmptyTitleText.Text = "No matching wallpapers";
            EmptySubtitleText.Text = "Try adjusting your search or filter.";
        }
        else
        {
            var parts = new List<string>(3)
            {
                $"{totalCount} {(totalCount == 1 ? "wallpaper" : "wallpapers")}",
                $"{favoriteCount} {(favoriteCount == 1 ? "favorite" : "favorites")}"
            };
            if (missingCount > 0)
            {
                parts.Add($"{missingCount} missing");
            }

            LibraryCountText.Text = string.Join(" • ", parts);
            EmptyTitleText.Text = "No wallpapers in library";
            EmptySubtitleText.Text = "Use Add Wallpaper, Add Folder, or drag & drop files here.";
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
        var loc = LocalizationService.Instance;
        var isTr = loc.CurrentLanguage == EffectiveLanguage.Turkish;

        if (!isUpdatingFeedbackText && StatusText != null && !string.IsNullOrEmpty(StatusText.Text) && isTr)
        {
            var localized = LocalizeStatusMessage(StatusText.Text);
            if (!string.Equals(localized, StatusText.Text, StringComparison.Ordinal))
            {
                try
                {
                    isUpdatingFeedbackText = true;
                    StatusText.Text = localized;
                }
                finally
                {
                    isUpdatingFeedbackText = false;
                }
            }
        }

        if (FeedbackArea == null) return;

        if (libraryController == null)
        {
            FeedbackArea.Visibility = string.IsNullOrWhiteSpace(StatusText?.Text) ? Visibility.Collapsed : Visibility.Visible;
            return;
        }
        var busy = libraryLoads > 0 || IsInteractionBusy;
        if (BusyIndicator != null)
        {
            BusyIndicator.IsActive = busy;
            BusyIndicator.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }
        if (BusyText != null)
        {
            BusyText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            BusyText.Text = libraryLoads > 0 && !IsInteractionBusy
                ? (isTr ? "Kütüphane yükleniyor…" : "Loading library…")
                : (isTr ? "İşleniyor…" : "Working…");
        }
        FeedbackArea.Visibility = busy || !string.IsNullOrWhiteSpace(StatusText?.Text)
            ? Visibility.Visible : Visibility.Collapsed;
        if (LibraryEmptyText != null)
        {
            LibraryEmptyText.Visibility = libraryLoads == 0 && libraryController.Items.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private string LocalizeStatusMessage(string? message)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;
        var loc = LocalizationService.Instance;
        if (loc.CurrentLanguage == EffectiveLanguage.English) return message;

        if (message.StartsWith("Applied wallpaper: ", StringComparison.Ordinal))
            return $"Duvar kağıdı uygulandı: {message["Applied wallpaper: ".Length..]}";
        if (message.StartsWith("Applying wallpaper: ", StringComparison.Ordinal))
            return $"Duvar kağıdı uygulanıyor: {message["Applying wallpaper: ".Length..]}";
        if (message.StartsWith("Rotated wallpaper: ", StringComparison.Ordinal))
            return $"Döngü uygulandı: {message["Rotated wallpaper: ".Length..]}";
        if (message.StartsWith("Added to library: ", StringComparison.Ordinal))
            return $"Kütüphaneye eklendi: {message["Added to library: ".Length..]}";
        if (message.StartsWith("Wallpaper is already in library: ", StringComparison.Ordinal))
            return $"Duvar kağıdı zaten kütüphanede mevcut: {message["Wallpaper is already in library: ".Length..]}";
        if (message.StartsWith("Performance report exported to ", StringComparison.Ordinal))
            return $"Performans raporu dışa aktarıldı: {message["Performance report exported to ".Length..]}";

        return message switch
        {
            "Runtime unavailable." => "Çalışma ortamı kullanılamıyor.",
            "File not found." => loc.GetString(LocalizationKeys.StatusWallpaperFileMissing),
            "Unsupported wallpaper." => loc.GetString(LocalizationKeys.StatusUnsupportedFile),
            "Failed to apply wallpaper." => "Duvar kağıdı uygulanamadı.",
            "Wallpaper stopped." => loc.GetString(LocalizationKeys.StatusWallpaperStopped),
            "Failed to load library." => "Kütüphane yüklenemedi.",
            "Rotation paused." => loc.GetString(LocalizationKeys.StatusRotationPaused),
            "Rotation stopped." => loc.GetString(LocalizationKeys.StatusRotationStopped),
            "Failed to select next wallpaper." => "Sonraki duvar kağıdı seçilemedi.",
            "Failed to export performance report." => "Performans raporu dışa aktarılamadı.",
            "Select a valid wallpaper file." => "Geçerli bir duvar kağıdı dosyası seçin.",
            "Wallpaper operation in progress." => "Duvar kağıdı işlemi sürüyor.",
            "No wallpapers found in folder." => "Klasörde duvar kağıdı bulunamadı.",
            "Import cancelled." => loc.GetString(LocalizationKeys.StatusImportCancelled),
            "Diagnostic log could not be opened." => "Tanılama günlüğü açılamadı.",
            "Runtime host recovered successfully." => loc.GetString(LocalizationKeys.StatusRuntimeRecovered),
            "Failed to restore previous wallpaper." => loc.GetString(LocalizationKeys.StatusRestoreFailed),
            "Wallpaper stopped safely after repeated failures." => loc.GetString(LocalizationKeys.StatusSafeStopped),
            "Invalid folder path." => "Geçersiz klasör yolu.",
            "Folder does not exist." => "Klasör bulunamadı.",
            "No supported wallpapers found." => "Desteklenen duvar kağıdı bulunamadı.",
            _ => message
        };
    }private void UpdateLibraryActionState()
    {
        if (closed) return;
        UpdateFeedbackVisuals();
        var isIdle = !IsInteractionBusy;
        var hasWallpaper = !string.IsNullOrWhiteSpace(activeWallpaperPath);

        AddWallpaperButton.IsEnabled = isIdle;
        AddFolderButton.IsEnabled = isIdle;
        AddFromUrlButton.IsEnabled = isIdle;
        SelectModeButton.IsEnabled = isIdle;
        SelectModeButton.Content = libraryController.IsSelectionMode ? LocalizationService.Instance.GetString(LocalizationKeys.LibDone) : LocalizationService.Instance.GetString(LocalizationKeys.LibSelect);
        BulkActionStrip.Visibility = libraryController.IsSelectionMode ? Visibility.Visible : Visibility.Collapsed;

        var hasSelection = libraryController.SelectedCount > 0;
        BulkFavoriteButton.IsEnabled = isIdle && hasSelection;
        BulkUnfavoriteButton.IsEnabled = isIdle && hasSelection;
        BulkRemoveButton.IsEnabled = isIdle && hasSelection;
        BulkRemoveButton.Content = hasSelection ? $"{LocalizationService.Instance.GetString(LocalizationKeys.LibRemove)} ({libraryController.SelectedCount})" : LocalizationService.Instance.GetString(LocalizationKeys.LibRemove);

        LibraryListView.IsEnabled = isIdle;
        LibrarySearchBox.IsEnabled = isIdle;
        LibraryFilterComboBox.IsEnabled = isIdle;
        LibrarySortComboBox.IsEnabled = isIdle;
        SelectImageButton.IsEnabled = isIdle;
        PreviousWallpaperButton.IsEnabled = isIdle;
        NextWallpaperButton.IsEnabled = isIdle;
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
                    locateButton.Content = LocalizationService.Instance.GetString(LocalizationKeys.ItemLocateFile);
            locateButton.IsEnabled = isIdle;
                }

                if (cardBorder.FindName("CardRemoveButton") is Button removeButton)
                {
                    removeButton.Content = LocalizationService.Instance.GetString(LocalizationKeys.LibRemove);
            removeButton.IsEnabled = currentEntry.CanRemove && isIdle;
                }

                if (cardBorder.FindName("CardSelectCheckBox") is CheckBox selectCheckBox)
                {
                    selectCheckBox.Visibility = libraryController.IsSelectionMode ? Visibility.Visible : Visibility.Collapsed;
                    selectCheckBox.IsChecked = libraryController.IsItemSelected(currentEntry.Id);
                }
            }
        }
    }

    private void UpdateMultiSelectUI()
    {
        if (closed) return;
        var isIdle = !IsInteractionBusy;
        var hasSelection = libraryController.SelectedCount > 0;
        BulkActionStrip.Visibility = libraryController.IsSelectionMode ? Visibility.Visible : Visibility.Collapsed;
        SelectModeButton.Content = libraryController.IsSelectionMode ? LocalizationService.Instance.GetString(LocalizationKeys.LibDone) : LocalizationService.Instance.GetString(LocalizationKeys.LibSelect);
        BulkFavoriteButton.IsEnabled = isIdle && hasSelection;
        BulkUnfavoriteButton.IsEnabled = isIdle && hasSelection;
        BulkRemoveButton.IsEnabled = isIdle && hasSelection;
        BulkRemoveButton.Content = hasSelection ? $"{LocalizationService.Instance.GetString(LocalizationKeys.LibRemove)} ({libraryController.SelectedCount})" : LocalizationService.Instance.GetString(LocalizationKeys.LibRemove);

        if (libraryController.IsSelectionMode)
        {
            var selected = libraryController.SelectedCount;
            LibraryCountText.Text = LocalizationService.Instance.Format(LocalizationKeys.ItemSelectedCount, selected);
        }

        foreach (var cardBorder in realizedCardBorders)
        {
            if (cardBorder.DataContext is WallpaperLibraryEntryView entry)
            {
                if (cardBorder.FindName("CardSelectCheckBox") is CheckBox selectCheckBox)
                {
                    selectCheckBox.Visibility = libraryController.IsSelectionMode ? Visibility.Visible : Visibility.Collapsed;
                    selectCheckBox.IsChecked = libraryController.IsItemSelected(entry.Id);
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

        var loc = LocalizationService.Instance;
        WallpaperTypeText.Text = !hasWallpaper
            ? (loc.CurrentLanguage == EffectiveLanguage.Turkish ? "Yok" : "None")
            : isVideo
                ? loc.GetString(LocalizationKeys.ActiveVideo)
                : loc.GetString(LocalizationKeys.ActiveImage);
        var measured = latestPerformanceStatus?.Snapshot;
        ActiveResolutionText.Text = hasWallpaper && measured != null &&
            string.Equals(measured.ActiveWallpaperPath, activeWallpaperPath, StringComparison.OrdinalIgnoreCase)
                ? FormatDimensions(isVideo ? measured.VideoWidth : measured.SourceWidth,
                    isVideo ? measured.VideoHeight : measured.SourceHeight)
                : (loc.CurrentLanguage == EffectiveLanguage.Turkish ? "Performans sekmesinde mevcut" : "Available in Performance");

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
                    ? loc.GetString(LocalizationKeys.ActivePaused)
                    : loc.GetString(LocalizationKeys.ActiveRunning);

        PauseResumeWallpaperButton.Content = resolvedPaused ? loc.GetString(LocalizationKeys.ActiveResume) : loc.GetString(LocalizationKeys.ActivePause);
        ActivePlaceholderIcon.Symbol = !hasWallpaper
            ? Symbol.SlideShow
            : isVideo
                ? Symbol.Video
                : Symbol.Pictures;

        _ = LoadWallpaperPreviewAsync(
            ActivePreviewImage,
            (isImage || isVideo) && !isMissingOnDisk ? activeWallpaperPath : null,
            isVideo: isVideo);
        UpdateRotationDetailsUI();
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

    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
    }

    private void SelectLanguageComboBoxItem(string? language)
    {
        if (LanguageComboBox == null) return;
        var expectedTag = AppLanguageExtensions.ParseLanguage(language).ToConfigString();

        for (var i = 0; i < LanguageComboBox.Items.Count; i++)
        {
            if (LanguageComboBox.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, expectedTag, StringComparison.OrdinalIgnoreCase))
            {
                LanguageComboBox.SelectedIndex = i;
                return;
            }
        }

        LanguageComboBox.SelectedIndex = 0;
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

    private async void OnEfficiencyProfileSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (isSyncingAppSettings || closed) return;
        if (EfficiencyProfileComboBox.SelectedItem is not ComboBoxItem selectedItem) return;

        var tag = selectedItem.Tag as string;
        var normalizedProfile = AppSettings.NormalizeEfficiencyProfile(tag) ?? "Balanced";
        UpdateEfficiencyProfileDescription(normalizedProfile);

        try
        {
            var current = await settingsService.LoadAsync();
            current.EfficiencyProfile = AppSettings.NormalizeEfficiencyProfile(tag);
            await settingsService.SaveAsync(current);

            _ = runtimeClient.SetEfficiencyProfileAsync(normalizedProfile);
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Error saving efficiency profile preference: {exception}");
        }
    }

    private void UpdateEfficiencyProfileDescription(string profile)
    {
        if (EfficiencyProfileDescriptionText == null) return;
        var isTr = LocalizationService.Instance.CurrentLanguage == EffectiveLanguage.Turkish;
        EfficiencyProfileDescriptionText.Text = profile switch
        {
            "Eco" => isTr
                ? "Eko: Agresif güç ve işlemci tasarrufu. Tam ekran pencerelerde, pil tasarrufunda, oturum kilitlendiğinde ve ekran kapandığında otomatik duraklar."
                : "Eco: Aggressive power and CPU savings. Automatically pauses during fullscreen windows, battery saver / low battery, workstation lock, and display sleep.",
            "Unrestricted" => isTr
                ? "Kısıtlamasız: Tam ekran uygulamalarda ve pilde dahi duvar kağıdını çalıştırmaya devam eder. Güvenlik için kilit ve ekran kapanmasında duraklar."
                : "Unrestricted: Keeps wallpaper playing even during fullscreen applications and battery power. Still pauses on workstation lock and display sleep for safety.",
            _ => isTr
                ? "Dengeli (Önerilen): Akıllı kaynak verimliliği. Tam ekran uygulamalar masaüstünü kapladığında veya pil tasarrufu devreye girdiğinde otomatik duraklar."
                : "Balanced (Recommended): Smart resource efficiency. Automatically pauses when fullscreen apps cover the desktop or battery saver is engaged."
        };
    }

    private void SelectEfficiencyProfileComboBoxItem(string? profile)
    {
        if (EfficiencyProfileComboBox == null) return;
        var expectedTag = AppSettings.NormalizeEfficiencyProfile(profile) ?? "Balanced";

        for (var i = 0; i < EfficiencyProfileComboBox.Items.Count; i++)
        {
            if (EfficiencyProfileComboBox.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, expectedTag, StringComparison.OrdinalIgnoreCase))
            {
                EfficiencyProfileComboBox.SelectedIndex = i;
                UpdateEfficiencyProfileDescription(expectedTag);
                return;
            }
        }

        EfficiencyProfileComboBox.SelectedIndex = 0;
        UpdateEfficiencyProfileDescription("Balanced");
    }

    private void SelectRotationModeComboBoxItem(string? mode)
    {
        if (RotationModeComboBox == null) return;
        var expectedTag = RotationModeExtensions.ParseMode(mode).ToString();
        for (var i = 0; i < RotationModeComboBox.Items.Count; i++)
        {
            if (RotationModeComboBox.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, expectedTag, StringComparison.OrdinalIgnoreCase))
            {
                RotationModeComboBox.SelectedIndex = i;
                return;
            }
        }
        RotationModeComboBox.SelectedIndex = 0;
    }

    private void SelectRotationOrderComboBoxItem(string? order)
    {
        if (RotationOrderComboBox == null) return;
        var expectedTag = RotationOrderExtensions.ParseOrder(order).ToString();
        for (var i = 0; i < RotationOrderComboBox.Items.Count; i++)
        {
            if (RotationOrderComboBox.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, expectedTag, StringComparison.OrdinalIgnoreCase))
            {
                RotationOrderComboBox.SelectedIndex = i;
                return;
            }
        }
        RotationOrderComboBox.SelectedIndex = 0;
    }

    private void SelectRotationIntervalComboBoxItem(int minutes)
    {
        if (RotationIntervalComboBox == null) return;
        var normalized = AppSettings.NormalizeRotationIntervalMinutes(minutes);
        var expectedTag = normalized.ToString();
        for (var i = 0; i < RotationIntervalComboBox.Items.Count; i++)
        {
            if (RotationIntervalComboBox.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, expectedTag, StringComparison.OrdinalIgnoreCase))
            {
                RotationIntervalComboBox.SelectedIndex = i;
                return;
            }
        }
        RotationIntervalComboBox.SelectedIndex = 2; // Default 30 min
    }

    private void UpdateRotationSettingsStatus(string? mode, string? order, int minutes)
    {
        if (RotationSettingsStatusText == null) return;
        var loc = LocalizationService.Instance;
        var isTr = loc.CurrentLanguage == EffectiveLanguage.Turkish;
        var parsedMode = RotationModeExtensions.ParseMode(mode);
        var parsedOrder = RotationOrderExtensions.ParseOrder(order);
        var normalizedMinutes = AppSettings.NormalizeRotationIntervalMinutes(minutes);

        if (parsedMode == RotationMode.Off)
        {
            RotationSettingsStatusText.Text = isTr ? "Döngü devre dışı." : "Rotation is disabled.";
        }
        else
        {
            var intervalText = normalizedMinutes switch
            {
                >= 60 => isTr ? $"{normalizedMinutes / 60} saat" : $"{normalizedMinutes / 60} hour(s)",
                _ => isTr ? $"{normalizedMinutes} dakika" : $"{normalizedMinutes} minutes"
            };
            var modeName = parsedMode == RotationMode.Favorites
                ? (isTr ? "Favoriler" : "Favorites")
                : (isTr ? "Tüm Kütüphane" : "All Library");
            var orderName = parsedOrder == RotationOrder.Shuffle
                ? (isTr ? "Karışık" : "Shuffle")
                : (isTr ? "Sıralı" : "Sequential");
            RotationSettingsStatusText.Text = isTr
                ? $"Aktif: {modeName} • {orderName} • Her {intervalText}."
                : $"Active: {modeName} • {orderName} every {intervalText}.";
        }
    }

    private async void OnRotationModeSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (isSyncingAppSettings || closed) return;
        if (RotationModeComboBox.SelectedItem is not ComboBoxItem selectedItem) return;

        var mode = selectedItem.Tag as string ?? "Off";
        var order = (RotationOrderComboBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "Sequential";
        var intervalMinutes = int.TryParse((RotationIntervalComboBox?.SelectedItem as ComboBoxItem)?.Tag as string, out var mins) ? mins : 30;

        UpdateRotationSettingsStatus(mode, order, intervalMinutes);

        try
        {
            var current = await settingsService.LoadAsync();
            current.RotationMode = mode;
            await settingsService.SaveAsync(current);
            await libraryController.Rotation.UpdateSettingsAsync(mode, order, intervalMinutes);
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Error saving rotation mode: {exception}");
        }
    }

    private async void OnRotationOrderSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (isSyncingAppSettings || closed) return;
        if (RotationOrderComboBox.SelectedItem is not ComboBoxItem selectedItem) return;

        var mode = (RotationModeComboBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "Off";
        var order = selectedItem.Tag as string ?? "Sequential";
        var intervalMinutes = int.TryParse((RotationIntervalComboBox?.SelectedItem as ComboBoxItem)?.Tag as string, out var mins) ? mins : 30;

        UpdateRotationSettingsStatus(mode, order, intervalMinutes);

        try
        {
            var current = await settingsService.LoadAsync();
            current.RotationOrder = order;
            await settingsService.SaveAsync(current);
            await libraryController.Rotation.UpdateSettingsAsync(mode, order, intervalMinutes);
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Error saving rotation order: {exception}");
        }
    }

    private async void OnRotationIntervalSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (isSyncingAppSettings || closed) return;
        if (RotationIntervalComboBox.SelectedItem is not ComboBoxItem selectedItem) return;

        var mode = (RotationModeComboBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "Off";
        var order = (RotationOrderComboBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "Sequential";
        var intervalMinutes = int.TryParse(selectedItem.Tag as string, out var mins) ? mins : 30;

        UpdateRotationSettingsStatus(mode, order, intervalMinutes);

        try
        {
            var current = await settingsService.LoadAsync();
            current.RotationIntervalMinutes = intervalMinutes;
            await settingsService.SaveAsync(current);
            await libraryController.Rotation.UpdateSettingsAsync(mode, order, intervalMinutes);
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Error saving rotation interval: {exception}");
        }
    }

    private void StartRotationCountdownTimer()
    {
        if (closed) return;
        if (rotationCountdownTimer == null)
        {
            rotationCountdownTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            rotationCountdownTimer.Tick += (_, _) => UpdateRotationCountdownUI();
        }
        if (!rotationCountdownTimer.IsEnabled)
        {
            rotationCountdownTimer.Start();
        }
        UpdateRotationCountdownUI();
    }

    private void StopRotationCountdownTimer()
    {
        if (rotationCountdownTimer != null && rotationCountdownTimer.IsEnabled)
        {
            rotationCountdownTimer.Stop();
        }
    }

    private void UpdateRotationDetailsUI(WallpaperRotationStatus? status = null)
    {
        if (closed) return;
        status ??= libraryController?.Rotation?.GetStatus();
        if (status == null) return;

        if (ActiveRotationStateText != null)
        {
            ActiveRotationStateText.Text = $"{LocalizationService.Instance.GetString(LocalizationKeys.ActiveRotation)}: {status.FormattedState}";
        }

        if (ActiveRotationPositionText != null)
        {
            ActiveRotationPositionText.Text = $"{LocalizationService.Instance.GetString(LocalizationKeys.ActivePosition)}: {status.FormattedPosition}";
        }

        UpdateRotationCountdownUI(status);
    }

    private void UpdateRotationCountdownUI(WallpaperRotationStatus? status = null)
    {
        if (ActiveRotationCountdownText == null || closed) return;
        var loc = LocalizationService.Instance;
        var nextLabel = loc.GetString(LocalizationKeys.ActiveNextChange);
        status ??= libraryController?.Rotation?.GetStatus();
        if (status == null || !status.IsActive)
        {
            var inactive = loc.CurrentLanguage == EffectiveLanguage.Turkish ? "Devre dışı" : "Inactive";
            ActiveRotationCountdownText.Text = $"{nextLabel}: {inactive}";
            return;
        }

        if (status.IsOverdue)
        {
            var overdue = loc.CurrentLanguage == EffectiveLanguage.Turkish ? "Gecikmiş (duraklatıldı)" : "Overdue (paused)";
            ActiveRotationCountdownText.Text = $"{nextLabel}: {overdue}";
            return;
        }

        if (status.NextDueTimeUtc.HasValue)
        {
            var remaining = status.NextDueTimeUtc.Value - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                var imminent = loc.CurrentLanguage == EffectiveLanguage.Turkish ? "Uygulanıyor..." : "Imminent";
                ActiveRotationCountdownText.Text = $"{nextLabel}: {imminent}";
            }
            else
            {
                var formatted = remaining.TotalHours >= 1
                    ? $"{(int)remaining.TotalHours}h {remaining.Minutes:D2}m {remaining.Seconds:D2}s"
                    : $"{remaining.Minutes:D2}m {remaining.Seconds:D2}s";
                if (remaining.TotalMinutes >= 1)
                {
                    var mins = (int)Math.Ceiling(remaining.TotalMinutes);
                    ActiveRotationCountdownText.Text = loc.Format(LocalizationKeys.FormatCountdownMin, mins);
                }
                else
                {
                    var secs = Math.Max(1, (int)remaining.TotalSeconds);
                    ActiveRotationCountdownText.Text = loc.Format(LocalizationKeys.FormatCountdownSec, secs);
                }
            }
        }
        else
        {
            var inactive = loc.CurrentLanguage == EffectiveLanguage.Turkish ? "Devre dışı" : "Inactive";
            ActiveRotationCountdownText.Text = $"{nextLabel}: {inactive}";
        }
    }

    private async void OnPreviousWallpaperClicked(object sender, RoutedEventArgs args)
    {
        if (IsInteractionBusy || closed) return;
        libraryBusy = true;
        UpdateLibraryActionState();
        try
        {
            StatusText.Text = "Selecting previous wallpaper...";
            var result = await libraryController.Rotation.RotatePreviousAsync(trigger: "Previous");
            if (result.Success && result.Item != null)
            {
                StatusText.Text = $"Applied wallpaper: {result.Item.DisplayName}";
                await SyncRuntimeStatusAsync();
            }
            else
            {
                StatusText.Text = result.Message;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error navigating to previous wallpaper: {ex}");
            StatusText.Text = "Failed to select previous wallpaper.";
        }
        finally
        {
            libraryBusy = false;
            if (!closed)
            {
                UpdateLibraryActionState();
                UpdateRotationDetailsUI();
            }
        }
    }

    private async void OnNextWallpaperClicked(object sender, RoutedEventArgs args)
    {
        if (IsInteractionBusy || closed) return;
        libraryBusy = true;
        UpdateLibraryActionState();
        try
        {
            StatusText.Text = "Selecting next wallpaper...";
            var result = await libraryController.Rotation.RotateNextAsync(trigger: "Next");
            if (result.Success && result.Item != null)
            {
                StatusText.Text = $"Applied wallpaper: {result.Item.DisplayName}";
                await SyncRuntimeStatusAsync();
            }
            else
            {
                StatusText.Text = result.Message;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error navigating to next wallpaper: {ex}");
            StatusText.Text = "Failed to select next wallpaper.";
        }
        finally
        {
            libraryBusy = false;
            if (!closed)
            {
                UpdateLibraryActionState();
                UpdateRotationDetailsUI();
            }
        }
    }

    private void OnRotationWallpaperRotated(object? sender, WallpaperRotationResult result)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (closed) return;
            if (result.Success && result.Item != null)
            {
                StatusText.Text = $"Rotated wallpaper: {result.Item.DisplayName}";
                _ = SyncRuntimeStatusAsync();
                RenderLibraryItems();
            }
            UpdateRotationDetailsUI();
        });
    }

    private void OnRotationStatusChanged(object? sender, WallpaperRotationStatus status)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (closed) return;
            UpdateRotationDetailsUI(status);
        });
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

            if (libraryRenderDeferred)
            {
                libraryRenderDeferred = false;
                RenderLibraryItems();
            }

            RefreshAutostartState();
            _ = LoadAppSettingsAsync();
            _ = LoadLibraryAsync();
            _ = SyncRuntimeStatusAsync();
            if (isPerformancePageActive)
            {
                StartPerformancePolling();
            }
            if (ActivePage?.Visibility == Visibility.Visible)
            {
                StartRotationCountdownTimer();
                UpdateRotationDetailsUI();
            }
        });
    }

    private void PerformAppExit()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            StopPerformancePolling();
            StopRotationCountdownTimer();
            libraryController.Dispose();
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
            StopRotationCountdownTimer();
            return;
        }

        if (isPerformancePageActive)
        {
            StartPerformancePolling();
        }

        if (ActivePage?.Visibility == Visibility.Visible)
        {
            StartRotationCountdownTimer();
            UpdateRotationDetailsUI();
        }

        RefreshAutostartState();
        _ = LoadLibraryAsync();
        _ = SyncRuntimeStatusAsync();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        StopPerformancePolling();
        StopRotationCountdownTimer();
        controller.OnWindowCloseRequested(out var cancelClose);
        args.Cancel = cancelClose;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        StopPerformancePolling();
        StopRotationCountdownTimer();
        closed = true;
        libraryController.Dispose();
        trayIcon.Dispose();
        App.ReleaseSingleInstance();
        LocalizationService.Instance.LanguageChanged -= OnLocalizationLanguageChanged;
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
        PerfSessionLockPolicyText.Text = snap.SessionLockedPauseActive ? "Active (Auto-paused by session lock)" : "Inactive (Session unlocked)";
        PerfDisplayPowerPolicyText.Text = snap.DisplayOffPauseActive ? "Active (Auto-paused by display off)" : "Inactive (Display active)";

        PerfEfficiencyProfileText.Text = snap.EfficiencyProfile ?? "Balanced";
        PerfRuntimeHealthText.Text = snap.RuntimeHealth ?? "Unknown";
        PerfRuntimeHealthText.Style = (Style)Application.Current.Resources[
            snap.RuntimeHealth == "Healthy" ? "HealthHealthyStyle"
            : snap.RuntimeHealth == "Degraded" ? "HealthWarningStyle"
            : snap.RuntimeHealth == "SafeStopped" ? "HealthAttentionStyle" : "HealthNeutralStyle"];
        PerfCurrentActionText.Text = snap.CurrentAction ?? "Operating normally";

        if (snap.RecentEvents != null && snap.RecentEvents.Count > 0)
        {
            PerfRecentEventsCountText.Text = $"Latest {Math.Min(6, snap.RecentEvents.Count)} of {snap.RecentEvents.Count}";
            var sb = new StringBuilder();
            foreach (var evt in snap.RecentEvents.OrderByDescending(item => item.TimestampUtc).Take(6))
            {
                sb.AppendLine($"{evt.EventType} · {evt.TimestampUtc.ToLocalTime():HH:mm:ss}\n{evt.Message}\n");
            }
            PerfRecentEventsListText.Text = sb.ToString().TrimEnd();
        }
        else
        {
            PerfRecentEventsCountText.Text = "0 events";
            PerfRecentEventsListText.Text = "No events recorded.";
        }

        if (snap.HasActiveWallpaper)
        {
            if (snap.IsPaused)
            {
                if (snap.FullscreenPauseActive || snap.PowerPauseActive || snap.SessionLockedPauseActive || snap.DisplayOffPauseActive)
                {
                    PerfEfficiencyStatusText.Text = "Efficiency: Paused to save resources";
                }
                else
                {
                    PerfEfficiencyStatusText.Text = "Efficiency: Paused (Manual)";
                }
            }
            else
            {
                PerfEfficiencyStatusText.Text = "Efficiency: Active";
            }
        }
        else
        {
            PerfEfficiencyStatusText.Text = "Efficiency: Idle (No wallpaper)";
        }

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
        PerfSessionLockPolicyText.Text = "Unavailable";
        PerfDisplayPowerPolicyText.Text = "Unavailable";
        PerfEfficiencyStatusText.Text = "Efficiency: Unavailable";

        PerfEfficiencyProfileText.Text = "Unknown";
        PerfRuntimeHealthText.Text = "Unavailable";
        PerfRuntimeHealthText.Style = (Style)Application.Current.Resources["HealthNeutralStyle"];
        PerfCurrentActionText.Text = "Runtime host disconnected";
        PerfRecentEventsCountText.Text = "0 events";
        PerfRecentEventsListText.Text = "Unavailable.";
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

    private void OnLocalizationLanguageChanged(EffectiveLanguage language)
    {
        if (closed) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateNavigationLocalization();
            UpdateLibraryLocalization();
            UpdateActiveWallpaperLocalization();
            UpdateSettingsLocalization();
            UpdatePerformanceLocalization();
        });
    }

    private void UpdateNavigationLocalization()
    {
        var loc = LocalizationService.Instance;
        LibraryNavigationItem.Content = loc.GetString(LocalizationKeys.NavLibrary);
        ActiveNavigationItem.Content = loc.GetString(LocalizationKeys.NavActiveWallpaper);
        PerformanceNavigationItem.Content = loc.GetString(LocalizationKeys.NavPerformance);
        SettingsNavigationItem.Content = loc.GetString(LocalizationKeys.NavSettings);
    }

    private void UpdateLibraryLocalization()
    {
        var loc = LocalizationService.Instance;
        if (LibraryTitleText != null)
        {
            LibraryTitleText.Text = loc.GetString(LocalizationKeys.NavLibrary);
        }

        if (LibrarySearchBox != null)
        {
            LibrarySearchBox.PlaceholderText = loc.GetString(LocalizationKeys.LibSearchPlaceholder);
            AutomationProperties.SetName(LibrarySearchBox, loc.GetString(LocalizationKeys.LibSearchPlaceholder));
        }

        if (AddWallpaperButton != null) AddWallpaperButton.Content = loc.GetString(LocalizationKeys.LibAddWallpaper);
        if (AddFolderButton != null) AddFolderButton.Content = loc.GetString(LocalizationKeys.LibAddFolder);
        if (AddFromUrlButton != null) AddFromUrlButton.Content = loc.GetString(LocalizationKeys.LibAddFromUrl);
        if (SelectModeButton != null)
        {
            SelectModeButton.Content = libraryController.IsSelectionMode ? loc.GetString(LocalizationKeys.LibDone) : loc.GetString(LocalizationKeys.LibSelect);
        }

        if (BulkSelectAllButton != null) BulkSelectAllButton.Content = loc.GetString(LocalizationKeys.LibSelectAll);
        if (BulkClearSelectionButton != null) BulkClearSelectionButton.Content = loc.GetString(LocalizationKeys.LibClear);
        if (BulkFavoriteButton != null) BulkFavoriteButton.Content = loc.GetString(LocalizationKeys.LibFavorite);
        if (BulkUnfavoriteButton != null) BulkUnfavoriteButton.Content = loc.GetString(LocalizationKeys.LibUnfavorite);
        if (BulkRemoveButton != null)
        {
            var hasSelection = libraryController.SelectedCount > 0;
            BulkRemoveButton.Content = hasSelection ? $"{loc.GetString(LocalizationKeys.LibRemove)} ({libraryController.SelectedCount})" : loc.GetString(LocalizationKeys.LibRemove);
        }
        if (BulkDoneButton != null) BulkDoneButton.Content = loc.GetString(LocalizationKeys.LibDone);

        if (EmptyTitleText != null) EmptyTitleText.Text = loc.GetString(LocalizationKeys.ItemEmptyTitle);
        if (EmptySubtitleText != null) EmptySubtitleText.Text = loc.GetString(LocalizationKeys.ItemEmptySubtitle);

        if (LibraryFilterComboBox?.Items != null)
        {
            foreach (var obj in LibraryFilterComboBox.Items)
            {
                if (obj is ComboBoxItem item && item.Tag is string tag)
                {
                    item.Content = tag switch
                    {
                        "All" => loc.GetString(LocalizationKeys.FilterAll),
                        "Images" => loc.GetString(LocalizationKeys.FilterImages),
                        "Videos" => loc.GetString(LocalizationKeys.FilterVideos),
                        "Favorites" => loc.GetString(LocalizationKeys.FilterFavorites),
                        "Recent" => loc.GetString(LocalizationKeys.FilterRecent),
                        _ => item.Content
                    };
                }
            }
        }

        if (LibrarySortComboBox?.Items != null)
        {
            foreach (var obj in LibrarySortComboBox.Items)
            {
                if (obj is ComboBoxItem item && item.Tag is string tag)
                {
                    item.Content = tag switch
                    {
                        "Newest" => loc.GetString(LocalizationKeys.SortRecent),
                        "Oldest" => loc.CurrentLanguage == EffectiveLanguage.Turkish ? "En Eski" : "Oldest",
                        "NameAsc" => loc.GetString(LocalizationKeys.SortName),
                        "NameDesc" => loc.CurrentLanguage == EffectiveLanguage.Turkish ? "İsim (Z–A)" : "Name (Z–A)",
                        _ => item.Content
                    };
                }
            }
        }

        RenderLibraryItems();
    }
    private void UpdateActiveWallpaperLocalization()
    {
        var loc = LocalizationService.Instance;
        if (ActiveWallpaperTitleText != null) ActiveWallpaperTitleText.Text = loc.GetString(LocalizationKeys.NavActiveWallpaper);
        if (PreviousWallpaperButton != null) PreviousWallpaperButton.Content = loc.GetString(LocalizationKeys.ActivePrevious);
        if (NextWallpaperButton != null) NextWallpaperButton.Content = loc.GetString(LocalizationKeys.ActiveNext);
        if (StopActiveWallpaperButton != null) StopActiveWallpaperButton.Content = loc.GetString(LocalizationKeys.ActiveStop);
        if (SelectImageButton != null) SelectImageButton.Content = loc.CurrentLanguage == EffectiveLanguage.Turkish ? "Dosya Seç…" : "Choose File…";
        if (RefreshActiveStatusButton != null) RefreshActiveStatusButton.Content = loc.CurrentLanguage == EffectiveLanguage.Turkish ? "Yenile" : "Refresh";

        UpdateActiveDetails(activeWallpaperPaused, activePlaybackState, statusKnown: true);
        UpdateRotationDetailsUI();
    }
    private void UpdateSettingsLocalization()
    {
        var loc = LocalizationService.Instance;
        var isTr = loc.CurrentLanguage == EffectiveLanguage.Turkish;

        if (SettingsTitleText != null) SettingsTitleText.Text = loc.GetString(LocalizationKeys.NavSettings);
        if (SettingsSubtitleText != null) SettingsSubtitleText.Text = isTr ? "Görünüm, verimlilik ve başlangıç tercihleri." : "Appearance and startup preferences.";

        if (SettingsAppearanceHeader != null) SettingsAppearanceHeader.Text = loc.GetString(LocalizationKeys.SetAppearance);
        if (SettingsAppearanceSubtitle != null) SettingsAppearanceSubtitle.Text = isTr ? "Açık, koyu veya Windows temanızı kullanın." : "Use light, dark, or your Windows theme.";

        if (SettingsLanguageHeader != null) SettingsLanguageHeader.Text = loc.GetString(LocalizationKeys.SetLanguage);
        if (SettingsLanguageSubtitle != null) SettingsLanguageSubtitle.Text = loc.GetString(LocalizationKeys.SetLanguageDescription);

        if (LanguageComboBox != null)
        {
            var systemTag = WindowsLanguageDetector.Instance.GetSystemDisplayLanguage();
            var effectiveForSystem = LanguageResolver.ResolveFromSystemTag(systemTag);
            var sysSuffix = effectiveForSystem == EffectiveLanguage.Turkish ? " (Türkçe)" : " (English)";
            foreach (var item in LanguageComboBox.Items.OfType<ComboBoxItem>())
            {
                var tag = item.Tag as string;
                item.Content = tag switch
                {
                    "System" => (isTr ? "Sistem varsayılanı" : "System default") + sysSuffix,
                    "Turkish" => "Türkçe",
                    "English" => "English",
                    _ => item.Content
                };
            }
        }

        if (ThemeComboBox != null)
        {
            foreach (var item in ThemeComboBox.Items.OfType<ComboBoxItem>())
            {
                var tag = item.Tag as string;
                item.Content = tag switch
                {
                    "System" => isTr ? "Sistem varsayılanı" : "System default",
                    "Light" => loc.GetString(LocalizationKeys.SetThemeLight),
                    "Dark" => loc.GetString(LocalizationKeys.SetThemeDark),
                    _ => item.Content
                };
            }
        }

        if (SettingsEfficiencyHeader != null) SettingsEfficiencyHeader.Text = loc.GetString(LocalizationKeys.SetEfficiency);
        if (SettingsEfficiencySubtitle != null) SettingsEfficiencySubtitle.Text = isTr
            ? "NiceWP'nin görsel akıcılık ile arka plan pil ve işlemci tasarrufu arasındaki dengesini ayarlayın."
            : "Control how NiceWP balances visual smoothness with background battery and CPU efficiency.";

        if (EfficiencyProfileComboBox != null)
        {
            foreach (var item in EfficiencyProfileComboBox.Items.OfType<ComboBoxItem>())
            {
                if (item.Content is StackPanel sp && sp.Children.Count >= 2 &&
                    sp.Children[0] is TextBlock title && sp.Children[1] is TextBlock desc)
                {
                    var tag = item.Tag as string;
                    switch (tag)
                    {
                        case "Balanced":
                            title.Text = isTr ? "Dengeli · Önerilen" : "Balanced · Recommended";
                            desc.Text = isTr ? "Dengeli masaüstü performansı ve arka plan tasarrufu" : "Balanced desktop performance and background savings";
                            break;
                        case "Eco":
                            title.Text = isTr ? "Eko" : "Eco";
                            desc.Text = isTr ? "Maksimum arka plan tasarrufu" : "Maximum background savings";
                            break;
                        case "Unrestricted":
                            title.Text = isTr ? "Kısıtlamasız" : "Unrestricted";
                            desc.Text = isTr ? "Duvar kağıdını mümkün olduğunca sürekli çalıştırır" : "Keeps wallpaper running whenever possible";
                            break;
                    }
                }
            }
            if (EfficiencyProfileComboBox.SelectedItem is ComboBoxItem selectedProfile)
            {
                UpdateEfficiencyProfileDescription(selectedProfile.Tag as string ?? "Balanced");
            }
        }

        if (SettingsStartupHeader != null) SettingsStartupHeader.Text = loc.GetString(LocalizationKeys.SetStartup);
        if (AutostartCheckBox != null) AutostartCheckBox.Content = loc.GetString(LocalizationKeys.SetStartupLaunch);

        if (SettingsWallpaperBehaviorHeader != null) SettingsWallpaperBehaviorHeader.Text = loc.GetString(LocalizationKeys.SetWallpaperBehavior);
        if (RestoreLastWallpaperCheckBox != null) RestoreLastWallpaperCheckBox.Content = loc.GetString(LocalizationKeys.SetRestoreLast);

        if (SettingsRotationHeader != null) SettingsRotationHeader.Text = loc.GetString(LocalizationKeys.SetWallpaperRotation);
        if (SettingsRotationSubtitle != null) SettingsRotationSubtitle.Text = isTr ? "Kütüphanenizdeki veya favorilerinizdeki duvar kağıtlarını otomatik döngüye sokun." : "Automatically cycle through wallpapers in your library or favorites.";

        if (SettingsRotationModeLabel != null) SettingsRotationModeLabel.Text = loc.GetString(LocalizationKeys.ActiveRotation);
        if (SettingsRotationOrderLabel != null) SettingsRotationOrderLabel.Text = loc.GetString(LocalizationKeys.SetRotationOrder);
        if (SettingsRotationIntervalLabel != null) SettingsRotationIntervalLabel.Text = loc.GetString(LocalizationKeys.SetChangeEvery);

        if (RotationModeComboBox != null)
        {
            foreach (var item in RotationModeComboBox.Items.OfType<ComboBoxItem>())
            {
                var tag = item.Tag as string;
                item.Content = tag switch
                {
                    "Off" => isTr ? "Kapalı" : "Off",
                    "All" => isTr ? "Tüm Kütüphane" : "All Library",
                    "Favorites" => isTr ? "Favoriler" : "Favorites",
                    _ => item.Content
                };
            }
        }

        if (RotationOrderComboBox != null)
        {
            foreach (var item in RotationOrderComboBox.Items.OfType<ComboBoxItem>())
            {
                var tag = item.Tag as string;
                item.Content = tag switch
                {
                    "Sequential" => loc.GetString(LocalizationKeys.SetRotationSequential),
                    "Shuffle" => loc.GetString(LocalizationKeys.SetRotationShuffle),
                    _ => item.Content
                };
            }
        }

        if (RotationIntervalComboBox != null)
        {
            foreach (var item in RotationIntervalComboBox.Items.OfType<ComboBoxItem>())
            {
                if (int.TryParse(item.Tag as string, out var mins))
                {
                    item.Content = mins switch
                    {
                        < 60 => isTr ? $"{mins} dakika" : $"{mins} minutes",
                        60 => isTr ? "1 saat" : "1 hour",
                        _ => isTr ? $"{mins / 60} saat" : $"{mins / 60} hours"
                    };
                }
            }
        }

        if (SettingsStorageHeader != null) SettingsStorageHeader.Text = isTr ? "Sistem Tepsisi ve Yerel Depolama" : "System Tray & Local Storage";
        if (SettingsStorageSubtitle != null) SettingsStorageSubtitle.Text = isTr
            ? "Bu pencereyi kapattığınızda NiceWP sistem tepsisinde çalışmaya devam eder. Duvar kağıdınız aktif kalır."
            : "NiceWP stays in the system tray when you close this window. Your wallpaper keeps running.";

        if (RotationModeComboBox?.SelectedItem is ComboBoxItem selMode &&
            RotationOrderComboBox?.SelectedItem is ComboBoxItem selOrder &&
            RotationIntervalComboBox?.SelectedItem is ComboBoxItem selInterval)
        {
            var mode = selMode.Tag as string ?? "Off";
            var order = selOrder.Tag as string ?? "Sequential";
            var mins = int.TryParse(selInterval.Tag as string, out var m) ? m : 30;
            UpdateRotationSettingsStatus(mode, order, mins);
        }
    }
    private void UpdatePerformanceLocalization()
    {
        var loc = LocalizationService.Instance;
        var isTr = loc.CurrentLanguage == EffectiveLanguage.Turkish;

        if (PerformanceTitleText != null) PerformanceTitleText.Text = loc.GetString(LocalizationKeys.NavPerformance);
        if (PerformanceSubtitleText != null) PerformanceSubtitleText.Text = isTr ? "Masaüstünüzün net görünümü. Metrikler bu cihazda kalır." : "A clear view of your desktop. Metrics stay on this device.";
        if (ExportReportButton != null) ExportReportButton.Content = loc.GetString(LocalizationKeys.PerfExport);

        if (PerfRuntimeHealthLabel != null) PerfRuntimeHealthLabel.Text = isTr ? "Çalışma Durumu" : "Runtime Health";
        if (PerfEfficiencyModeLabel != null) PerfEfficiencyModeLabel.Text = isTr ? "Verimlilik Modu" : "Efficiency Mode";
        if (PerfCurrentActionLabel != null) PerfCurrentActionLabel.Text = loc.GetString(LocalizationKeys.PerfCurrentAction);

        if (PerfCpuLabel != null) PerfCpuLabel.Text = loc.GetString(LocalizationKeys.PerfCpu);
        if (PerfWorkingSetLabel != null) PerfWorkingSetLabel.Text = loc.GetString(LocalizationKeys.PerfWorkingSet);
        if (PerfPrivateMemoryLabel != null) PerfPrivateMemoryLabel.Text = loc.GetString(LocalizationKeys.PerfPrivateMemory);
        if (PerfThreadsLabel != null) PerfThreadsLabel.Text = loc.GetString(LocalizationKeys.PerfThreads);
        if (PerfHandlesLabel != null) PerfHandlesLabel.Text = loc.GetString(LocalizationKeys.PerfHandles);
        if (PerfUptimeLabel != null) PerfUptimeLabel.Text = loc.GetString(LocalizationKeys.PerfUptime);

        if (PerfWallpaperRendererHeader != null) PerfWallpaperRendererHeader.Text = isTr ? "Duvar Kağıdı ve İşleyici" : "Wallpaper & Renderer";
        if (PerfActiveTypeLabel != null) PerfActiveTypeLabel.Text = isTr ? "Aktif Tür ve Oynatma" : "Active Type & Playback";
        if (PerfRendererWindowLabel != null) PerfRendererWindowLabel.Text = isTr ? "İşleyici Penceresi (HWND)" : "Renderer Window (HWND)";
        if (PerfSourceResolutionLabel != null) PerfSourceResolutionLabel.Text = isTr ? "Kaynak Çözünürlüğü" : "Source Resolution";
        if (PerfDecodeResolutionLabel != null) PerfDecodeResolutionLabel.Text = isTr ? "Çözümleme Çözünürlüğü ve Bellek" : "Decode Resolution & Memory";
        if (PerfMediaDetailsLabel != null) PerfMediaDetailsLabel.Text = isTr ? "Medya Ayrıntıları" : "Media Details";

        if (PerfGuardrailsHeader != null) PerfGuardrailsHeader.Text = isTr ? "Masaüstü Korumaları" : "Desktop guardrails";
        if (PerfDesktopHostLabel != null) PerfDesktopHostLabel.Text = isTr ? "Masaüstü Ana Bilgisayarı (WorkerW)" : "Desktop Host (WorkerW)";
        if (PerfPauseReasonsLabel != null) PerfPauseReasonsLabel.Text = isTr ? "Aktif Duraklatma Nedenleri" : "Active Pause Reasons";
        if (PerfFullscreenPolicyLabel != null) PerfFullscreenPolicyLabel.Text = isTr ? "Tam Ekran İlkesi" : "Fullscreen Policy";
        if (PerfPowerPolicyLabel != null) PerfPowerPolicyLabel.Text = isTr ? "Güç / Pil İlkesi" : "Power / Battery Policy";
        if (PerfSessionLockPolicyLabel != null) PerfSessionLockPolicyLabel.Text = isTr ? "Oturum Kilidi İlkesi" : "Session Lock Policy";
        if (PerfDisplayPowerPolicyLabel != null) PerfDisplayPowerPolicyLabel.Text = isTr ? "Ekran Güç İlkesi" : "Display Power Policy";

        if (PerfAuditTrailHeader != null) PerfAuditTrailHeader.Text = isTr ? "Denetim Kaydı" : "Audit Trail";
        if (PerfGpuHeader != null) PerfGpuHeader.Text = isTr ? "GPU Ölçümü" : "GPU Measurement";
        if (PerfPrivacyHeader != null) PerfPrivacyHeader.Text = isTr ? "Gizlilik ve Yerel Şeffaflık" : "Privacy & Local Transparency";
        if (PerfPrivacySubtitle != null) PerfPrivacySubtitle.Text = isTr ? "Metrikler bu cihazda kalır. Hiçbir telemetri gönderilmez." : "Metrics stay on this device. No performance telemetry is sent.";
    }

    private static string FormatDimensions(uint? w, uint? h) =>
        w.HasValue && h.HasValue ? $"{w.Value} x {h.Value}" : "Unavailable";
}
