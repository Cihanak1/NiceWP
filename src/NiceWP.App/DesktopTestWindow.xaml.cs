using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
using NiceWP.Infrastructure.Scheduling;
using NiceWP.Infrastructure.Adaptive;
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
    private readonly AppRuleMatchingService appRuleMatchingService = new();
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
        InitializeLanguage();
        InitializeComponent();
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
        libraryController.Schedule.StatusChanged += OnScheduleStatusChanged;
        libraryController.Rotation.RotationPrecondition = () =>
        {
            var p = trayIcon?.CurrentProfile ?? NiceWP.Core.AdaptiveDesktopProfile.Automatic;
            return p != NiceWP.Core.AdaptiveDesktopProfile.Presentation &&
                   p != NiceWP.Core.AdaptiveDesktopProfile.Gaming &&
                   p != NiceWP.Core.AdaptiveDesktopProfile.Cinema;
        };
        libraryController.Schedule.SchedulePrecondition = () =>
        {
            var p = trayIcon?.CurrentProfile ?? NiceWP.Core.AdaptiveDesktopProfile.Automatic;
            return p != NiceWP.Core.AdaptiveDesktopProfile.Presentation &&
                   p != NiceWP.Core.AdaptiveDesktopProfile.Gaming;
        };

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
        trayIcon.ProfileSelected += OnTrayProfileSelected;
        trayIcon.BudgetSelected += OnTrayBudgetSelected;
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

        LocalizationService.Instance.LanguageChanged += OnLocalizationLanguageChanged;
        UpdateNavigationLocalization();
        UpdateLibraryLocalization();
        UpdateActiveWallpaperLocalization();
        UpdateSettingsLocalization();
        UpdatePerformanceLocalization();

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
                SelectLanguageComboBoxItem(settings.Language);
                SelectThemeComboBoxItem(settings.Theme);
                SelectEfficiencyProfileComboBoxItem(settings.EfficiencyProfile);
                SelectRotationModeComboBoxItem(settings.RotationMode);
                SelectRotationOrderComboBoxItem(settings.RotationOrder);
                SelectRotationIntervalComboBoxItem(settings.RotationIntervalMinutes);
                UpdateRotationSettingsStatus(settings.RotationMode, settings.RotationOrder, settings.RotationIntervalMinutes);
                SelectScheduleSettingsItems(settings);
            }
            finally
            {
                isSyncingAppSettings = false;
            }

            await libraryController.Rotation.InitializeAsync(settings);
            UpdateRotationDetailsUI();
            libraryController.Schedule.Initialize(settings);
            UpdateScheduleDetailsUI();
            appRuleMatchingService.UpdateRules(settings.AppRules);
            RenderAppRules();
            if (settings.EffectiveScheduleMode == ScheduleModeExtensions.ModeDayNight)
            {
                _ = libraryController.Schedule.EvaluateAndApplyCurrentSlotAsync();
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
                libraryController?.Schedule?.NotifyPauseState(status.IsPaused);

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
            return LocalizationService.Instance.CurrentLanguage == EffectiveLanguage.Turkish ? "Etkin duvar kağıdı yok" : "No wallpaper active";
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
        var urlBox = (TextBox)contentPanel.FindName("RemoteUrlBox")!;
        var applyCheckBox = (CheckBox)contentPanel.FindName("RemoteApplyCheckBox")!;
        var progressBar = (ProgressBar)contentPanel.FindName("RemoteProgressBar")!;
        var statusBlock = (TextBlock)contentPanel.FindName("RemoteStatusText")!;
        var errorBlock = (TextBlock)contentPanel.FindName("RemoteErrorText")!;
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
        if (contentPanel.FindName("RemoteInstructionsText") is TextBlock instructions)
        {
            instructions.Text = isTrRemote
                ? "Doğrudan bir medya veya desteklenen duvar kağıdı sayfası bağlantısı yapıştırın."
                : "Paste a direct media or supported wallpaper page URL.";
        }
        if (contentPanel.FindName("RemoteSecurityHeader") is TextBlock secHeader)
        {
            secHeader.Text = isTrRemote
                ? "Yerel olarak indirilir ve kullanılmadan önce doğrulanır."
                : "Downloaded locally and validated before use.";
        }
        if (contentPanel.FindName("RemoteSecurityText") is TextBlock secText)
        {
            secText.Text = isTrRemote
                ? "NiceWP, yalnızca siz açıkça bir uzak duvar kağıdı içe aktardığınızda ağa erişir."
                : "NiceWP only accesses the network when you explicitly import a remote wallpaper.";
        }
        urlBox.Header = isTrRemote ? "Duvar Kağıdı URL'si" : "Wallpaper URL";
        AutomationProperties.SetName(urlBox, isTrRemote ? "Duvar Kağıdı URL'si" : "Wallpaper URL");
        applyCheckBox.Content = isTrRemote ? "İndirdikten sonra uygula" : "Apply after download";

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
                errorBlock.Text = isTrRemote ? "Geçersiz URL." : "Invalid URL.";
                errorBlock.Visibility = Visibility.Visible;
                return;
            }

            errorBlock.Visibility = Visibility.Collapsed;
            urlBox.IsEnabled = false;
            applyCheckBox.IsEnabled = false;
            dialog.IsPrimaryButtonEnabled = false;
            progressBar.Visibility = Visibility.Visible;
            progressBar.IsIndeterminate = true;
            statusBlock.Text = isTrRemote ? "Bağlanılıyor..." : "Connecting...";

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
                    StatusText.Text = isTrRemote
                        ? (result.Message != null ? LocalizeStatusMessage(result.Message) : "Kütüphaneye eklendi.")
                        : (result.Message ?? "Added to library.");
                }
                else if (result.Message == "Download cancelled.")
                {
                    dialog.Hide();
                    StatusText.Text = isTrRemote ? "İndirme iptal edildi." : "Download cancelled.";
                }
                else
                {
                    progressBar.Visibility = Visibility.Collapsed;
                    urlBox.IsEnabled = true;
                    applyCheckBox.IsEnabled = true;
                    dialog.IsPrimaryButtonEnabled = true;
                    errorBlock.Text = isTrRemote
                        ? (result.Message != null ? LocalizeStatusMessage(result.Message) : "Sunucuya bağlanılamadı.")
                        : (result.Message ?? "Could not connect to server.");
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

        var loc = LocalizationService.Instance;
        var isTr = loc.CurrentLanguage == EffectiveLanguage.Turkish;

        if (cardBorder.FindName("CardStateBadge") is Border badge &&
            cardBorder.FindName("CardStateBadgeText") is TextBlock badgeText)
        {
            if (currentEntry.IsMissing)
            {
                badge.Visibility = Visibility.Visible;
                badgeText.Text = isActive
                    ? (isTr ? "AKTİF · EKSİK" : "ACTIVE · MISSING")
                    : (isTr ? "EKSİK" : "MISSING");
            }
            else if (isActive)
            {
                badge.Visibility = Visibility.Visible;
                badgeText.Text = isTr ? "AKTİF" : "ACTIVE";
            }
            else if (currentEntry.IsRemoteImported)
            {
                badge.Visibility = Visibility.Visible;
                badgeText.Text = isTr ? "UZAK · YEREL KOPYA" : "REMOTE · LOCAL COPY";
            }
            else
            {
                badge.Visibility = Visibility.Collapsed;
            }
            badgeText.Style = (Style)Application.Current.Resources[
                isActive ? "ActiveChipTextStyle" : "NeutralChipTextStyle"];
            ToolTipService.SetToolTip(badge, currentEntry.IsRemoteImported && !currentEntry.IsMissing
                ? (isTr ? "Yerel olarak indirildi ve doğrulandı." : "Downloaded and validated locally.")
                : badgeText.Text);
        }

        if (cardBorder.FindName("CardCostBadgeText") is TextBlock costText)
        {
            costText.Text = currentEntry.GetLocalizedCostLabel(isTr);
            if (cardBorder.FindName("CardCostBadge") is Border costBadge)
            {
                var costTip = isTr
                    ? $"Donanım yükü: {currentEntry.GetLocalizedCostLabel(isTr)}"
                    : $"Hardware footprint: {currentEntry.CostLabel}";
                ToolTipService.SetToolTip(costBadge, costTip);
            }
        }

        ToolTipService.SetToolTip(cardBorder, currentEntry.IsRemoteImported
            ? $"{currentEntry.DisplayName}\n{(isTr ? "UZAK · YEREL KOPYA\nYerel olarak indirildi ve doğrulandı." : "REMOTE · LOCAL COPY\nDownloaded and validated locally.")}"
            : currentEntry.GetLocalizedToolTipText(isTr));
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

        var isTr = LocalizationService.Instance.CurrentLanguage == EffectiveLanguage.Turkish;
        ToolTipService.SetToolTip(button, isFav
            ? (isTr ? "Favorilerden çıkar" : "Remove from favorites")
            : (isTr ? "Favorilere ekle" : "Add to favorites"));
        AutomationProperties.SetName(button, isFav
            ? (isTr ? $"{entry.DisplayName} favorilerden çıkar" : $"Remove {entry.DisplayName} from favorites")
            : (isTr ? $"{entry.DisplayName} favorilere ekle" : $"Add {entry.DisplayName} to favorites"));
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
        var isTr = LocalizationService.Instance.CurrentLanguage == EffectiveLanguage.Turkish;
        AutomationProperties.SetName(button, isTr ? $"{currentEntry.DisplayName} uygula" : $"Apply {currentEntry.DisplayName}");
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
        var isTr = LocalizationService.Instance.CurrentLanguage == EffectiveLanguage.Turkish;
        AutomationProperties.SetName(button, isTr ? $"{currentEntry.DisplayName} için dosya bul" : $"Locate file for {currentEntry.DisplayName}");
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
        var isTr = LocalizationService.Instance.CurrentLanguage == EffectiveLanguage.Turkish;
        AutomationProperties.SetName(button, isTr ? $"{currentEntry.DisplayName} kaldır" : $"Remove {currentEntry.DisplayName}");
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
        if (closed || libraryRenderDeferred || libraryController == null) return;

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

        var loc = LocalizationService.Instance;
        var isTr = loc.CurrentLanguage == EffectiveLanguage.Turkish;

        if (libraryController.IsSelectionMode)
        {
            var selected = libraryController.SelectedCount;
            LibraryCountText.Text = loc.Format(LocalizationKeys.ItemSelectedCount, selected);
        }
        else if (totalCount == 0)
        {
            LibraryCountText.Text = isTr ? "Koleksiyonda 0 duvar kağıdı" : "0 wallpapers in collection";
            EmptyTitleText.Text = loc.GetString(LocalizationKeys.ItemEmptyTitle);
            EmptySubtitleText.Text = isTr ? "Duvar Kağıdı Ekle, Klasör Ekle veya dosyaları buraya sürükleyip bırakın." : "Use Add Wallpaper, Add Folder, or drag & drop files here.";
        }
        else if (isFilteredOrSearched)
        {
            var parts = new List<string>(3)
            {
                isTr ? $"{totalCount} duvar kağıdından {count} tanesi gösteriliyor" : $"Showing {count} of {totalCount} wallpapers"
            };
            if (favoriteCount > 0)
            {
                parts.Add(isTr ? $"{favoriteCount} favori" : $"{favoriteCount} favorites");
            }
            if (missingCount > 0)
            {
                parts.Add(isTr ? $"{missingCount} diskte eksik" : $"{missingCount} missing on disk");
            }

            LibraryCountText.Text = string.Join(" • ", parts);
            EmptyTitleText.Text = isTr ? "Eşleşen duvar kağıdı bulunamadı" : "No matching wallpapers";
            EmptySubtitleText.Text = isTr ? "Aramanızı veya filtrenizi değiştirmeyi deneyin." : "Try adjusting your search or filter.";
        }
        else
        {
            var parts = new List<string>(3)
            {
                isTr ? $"{totalCount} duvar kağıdı" : $"{totalCount} {(totalCount == 1 ? "wallpaper" : "wallpapers")}",
                isTr ? $"{favoriteCount} favori" : $"{favoriteCount} {(favoriteCount == 1 ? "favorite" : "favorites")}"
            };
            if (missingCount > 0)
            {
                parts.Add(isTr ? $"{missingCount} eksik" : $"{missingCount} missing");
            }

            LibraryCountText.Text = string.Join(" • ", parts);
            EmptyTitleText.Text = loc.GetString(LocalizationKeys.ItemEmptyTitle);
            EmptySubtitleText.Text = isTr ? "Duvar Kağıdı Ekle, Klasör Ekle veya dosyaları buraya sürükleyip bırakın." : "Use Add Wallpaper, Add Folder, or drag & drop files here.";
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
        PopulateScheduleWallpaperComboBoxes();
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
        if (message.StartsWith("Renamed wallpaper: ", StringComparison.Ordinal))
            return $"Duvar kağıdı yeniden adlandırıldı: {message["Renamed wallpaper: ".Length..]}";
        if (message.StartsWith("Relinked wallpaper: ", StringComparison.Ordinal))
            return $"Duvar kağıdı yeniden bağlandı: {message["Relinked wallpaper: ".Length..]}";
        if (message.StartsWith("Removed from library: ", StringComparison.Ordinal))
            return $"Kütüphaneden kaldırıldı: {message["Removed from library: ".Length..]}";
        if (message.StartsWith("Schedule transition: ", StringComparison.Ordinal))
            return $"Program geçişi: {message["Schedule transition: ".Length..]}";

        var matchRemoved = Regex.Match(message, @"^Removed (\d+) wallpapers? from library\.$");
        if (matchRemoved.Success)
            return $"{matchRemoved.Groups[1].Value} duvar kağıdı kütüphaneden kaldırıldı.";

        var matchFav = Regex.Match(message, @"^(\d+) wallpapers? favorited\.$");
        if (matchFav.Success)
            return $"{matchFav.Groups[1].Value} duvar kağıdı favorilere eklendi.";

        var matchUnfav = Regex.Match(message, @"^(\d+) wallpapers? unfavorited\.$");
        if (matchUnfav.Success)
            return $"{matchUnfav.Groups[1].Value} duvar kağıdı favorilerden çıkarıldı.";

        var matchImported = Regex.Match(message, @"^Added (\d+) wallpapers?, skipped (\d+) duplicates?\.$");
        if (matchImported.Success)
            return $"{matchImported.Groups[1].Value} duvar kağıdı eklendi, {matchImported.Groups[2].Value} mükerrer atlandı.";

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
            "Removed from library." => "Kütüphaneden kaldırıldı.",
            "No wallpapers selected." => "Hiçbir duvar kağıdı seçilmedi.",
            "All wallpapers in folder already in library." => "Klasördeki tüm duvar kağıtları zaten kütüphanede mevcut.",
            "Added to library." => "Kütüphaneye eklendi.",
            "Download cancelled." => "İndirme iptal edildi.",
            "Could not connect to server." => "Sunucuya bağlanılamadı.",
            "Invalid URL." => "Geçersiz URL.",
            "Day wallpaper applied." => "Gündüz duvar kağıdı uygulandı.",
            "Night wallpaper applied." => "Gece duvar kağıdı uygulandı.",
            "No day wallpaper configured." => "Gündüz duvar kağıdı ayarlanmadı.",
            "No night wallpaper configured." => "Gece duvar kağıdı ayarlanmadı.",
            "No eligible wallpapers to rotate." => "Döngü için uygun duvar kağıdı bulunamadı.",
            "No eligible wallpapers." => "Uygun duvar kağıdı yok.",
            "No eligible favorites to rotate." => "Döngü için uygun favori duvar kağıdı bulunamadı.",
            "No favorites in library to rotate." => "Döngü için kütüphanede favori duvar kağıdı yok.",
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

        var loc = LocalizationService.Instance;
        var isTr = loc.CurrentLanguage == EffectiveLanguage.Turkish;

        PathText.Text = FormatWallpaperDisplay(activeWallpaperPath, activeWallpaperType);
        ToolTipService.SetToolTip(PathText, hasWallpaper ? activeWallpaperPath : null);

        ActiveFullPathText.Text = hasWallpaper
            ? activeWallpaperPath!
            : (isTr ? "Kütüphaneden bir duvar kağıdı seçin veya aşağıdan bir dosya belirleyin." : "Select a wallpaper from the Library or choose a file below.");
        ActiveMissingWarningText.Text = isTr ? "Duvar kağıdı dosyası bulunamadı." : "The wallpaper file is missing.";
        ActiveMissingWarningText.Visibility = isMissingOnDisk ? Visibility.Visible : Visibility.Collapsed;
        ActiveEmptyState.Visibility = hasWallpaper ? Visibility.Collapsed : Visibility.Visible;
        ActiveDetailsPanel.Visibility = hasWallpaper ? Visibility.Visible : Visibility.Collapsed;

        WallpaperTypeText.Text = !hasWallpaper
            ? (isTr ? "Yok" : "None")
            : isVideo
                ? loc.GetString(LocalizationKeys.ActiveVideo)
                : loc.GetString(LocalizationKeys.ActiveImage);
        var measured = latestPerformanceStatus?.Snapshot;
        ActiveResolutionText.Text = hasWallpaper && measured != null &&
            string.Equals(measured.ActiveWallpaperPath, activeWallpaperPath, StringComparison.OrdinalIgnoreCase)
                ? FormatDimensions(isVideo ? measured.VideoWidth : measured.SourceWidth,
                    isVideo ? measured.VideoHeight : measured.SourceHeight)
                : (isTr ? "Performans sekmesinde mevcut" : "Available in Performance");

        var resolvedPaused = isPaused ||
            string.Equals(playbackState, IpcConstants.PlaybackStatePaused, StringComparison.OrdinalIgnoreCase);
        activeWallpaperPaused = resolvedPaused;
        activePlaybackState = hasWallpaper
            ? (resolvedPaused ? IpcConstants.PlaybackStatePaused : IpcConstants.PlaybackStateRunning)
            : IpcConstants.PlaybackStateNone;

        PlaybackStatusText.Text = !statusKnown
            ? (isTr ? "Kullanılamıyor" : "Unavailable")
            : !hasWallpaper
                ? (isTr ? "Yok" : "None")
                : resolvedPaused
                    ? loc.GetString(LocalizationKeys.ActivePaused)
                    : loc.GetString(LocalizationKeys.ActiveRunning);

        if (ActiveEfficiencyProfileText != null)
        {
            var profileTag = (EfficiencyProfileComboBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "Balanced";
            ActiveEfficiencyProfileText.Text = PerformanceSnapshotFormatter.FormatEfficiencyProfile(profileTag, loc);
        }

        if (ActiveWallpaperCostText != null)
        {
            if (!hasWallpaper)
            {
                ActiveWallpaperCostText.Text = string.Empty;
                ActiveWallpaperCostText.Visibility = Visibility.Collapsed;
            }
            else
            {
                ActiveWallpaperCostText.Visibility = Visibility.Visible;
                long? size = null;
                try
                {
                    if (File.Exists(activeWallpaperPath))
                    {
                        size = new FileInfo(activeWallpaperPath).Length;
                    }
                }
                catch { }

                var cost = WallpaperCostClassifier.Classify(activeWallpaperType, fileSizeBytes: size);
                var costLabel = cost switch
                {
                    WallpaperCost.Light => isTr ? "Yük: Hafif" : "Cost: Light",
                    WallpaperCost.Moderate => isTr ? "Yük: Orta" : "Cost: Moderate",
                    WallpaperCost.Heavy => isTr ? "Yük: Ağır" : "Cost: Heavy",
                    _ => "Light"
                };
                ActiveWallpaperCostText.Text = costLabel;
                ToolTipService.SetToolTip(ActiveWallpaperCostText, isTr
                    ? $"Donanım tüketim sınıfı: {costLabel}"
                    : $"Hardware resource tier: {costLabel}");
            }
        }

        if (ActiveAdaptiveProfileText != null)
        {
            var p = trayIcon?.CurrentProfile ?? NiceWP.Core.AdaptiveDesktopProfile.Automatic;
            var pLabel = p switch
            {
                NiceWP.Core.AdaptiveDesktopProfile.Automatic => isTr ? "Profil: Otomatik" : "Profile: Auto",
                NiceWP.Core.AdaptiveDesktopProfile.Normal => isTr ? "Profil: Normal" : "Profile: Normal",
                NiceWP.Core.AdaptiveDesktopProfile.Focus => isTr ? "Profil: Odak" : "Profile: Focus",
                NiceWP.Core.AdaptiveDesktopProfile.Gaming => isTr ? "Profil: Oyun" : "Profile: Gaming",
                NiceWP.Core.AdaptiveDesktopProfile.Presentation => isTr ? "Profil: Sunum" : "Profile: Presentation",
                NiceWP.Core.AdaptiveDesktopProfile.BatterySaver => isTr ? "Profil: Pil Tasarrufu" : "Profile: Battery Saver",
                NiceWP.Core.AdaptiveDesktopProfile.Cinema => isTr ? "Profil: Sinema" : "Profile: Cinema",
                _ => p.ToString()
            };
            ActiveAdaptiveProfileText.Text = pLabel;
            ToolTipService.SetToolTip(ActiveAdaptiveProfileText, isTr
                ? $"Etkin masaüstü profili: {pLabel}"
                : $"Effective desktop profile: {pLabel}");
        }

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

    private async void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (isSyncingAppSettings || closed) return;
        if (LanguageComboBox?.SelectedItem is not ComboBoxItem selectedItem) return;

        var tag = selectedItem.Tag as string ?? "System";
        var preferredLang = AppLanguageExtensions.ParseLanguage(tag);
        var effective = new LanguageResolver().Resolve(preferredLang);

        LocalizationService.Instance.SetLanguage(effective);

        try
        {
            var current = await settingsService.LoadAsync();
            current.Language = tag == "System" ? null : tag;
            await settingsService.SaveAsync(current);
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Error saving language preference: {exception}");
        }
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
        if (closed || libraryController?.Rotation == null) return;
        status ??= libraryController.Rotation.GetStatus();
        if (status == null) return;

        var loc = LocalizationService.Instance;
        var isTr = loc.CurrentLanguage == EffectiveLanguage.Turkish;

        if (ActiveRotationStateText != null)
        {
            var rotState = status.Mode switch
            {
                RotationMode.All => isTr ? $"Tüm Kütüphane • {(status.Order == RotationOrder.Shuffle ? "Karışık" : "Sıralı")}" : $"All Library • {status.Order.ToDisplayString()}",
                RotationMode.Favorites => isTr ? $"Favoriler • {(status.Order == RotationOrder.Shuffle ? "Karışık" : "Sıralı")}" : $"Favorites • {status.Order.ToDisplayString()}",
                _ => isTr ? "Kapalı" : "Off"
            };
            ActiveRotationStateText.Text = $"{loc.GetString(LocalizationKeys.ActiveRotation)}: {rotState}";
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
        latestPerformanceStatus = status;
        var snap = status.Snapshot;
        var stats = status.Stats;
        var loc = LocalizationService.Instance;
        var isTr = loc.CurrentLanguage == EffectiveLanguage.Turkish;
        var culture = loc.Culture;

        PerformanceHostStatusTitle.Text = PerformanceSnapshotFormatter.FormatHostStatusTitle(isConnected: true, isUnavailable: false, loc);
        PerformanceHostStatusDetails.Text = PerformanceSnapshotFormatter.FormatHostDetails(snap.ProcessId, snap.Uptime, loc);
        PerformanceLiveIndicator.Text = PerformanceSnapshotFormatter.FormatLiveIndicator(loc);
        PerformanceLiveIndicator.Visibility = Visibility.Visible;

        PerfCpuCurrentText.Text = stats.CurrentCpuPercent.HasValue ? $"{stats.CurrentCpuPercent.Value.ToString("F1", culture)}%" : "--%";
        PerfWorkingSetText.Text = FormatMegabytes(snap.WorkingSetBytes);
        PerfPrivateMemoryText.Text = FormatMegabytes(snap.PrivateMemoryBytes);
        PerfThreadsText.Text = snap.ThreadCount.ToString();
        PerfHandlesText.Text = snap.HandleCount.ToString();
        PerfUptimeText.Text = PerformanceSnapshotFormatter.FormatTimeSpan(snap.Uptime);

        PerfSampleCountText.Text = PerformanceSnapshotFormatter.FormatSampleCount(stats.SampleCount, loc);
        var cpuAvg = stats.AverageCpuPercent.HasValue ? $"{stats.AverageCpuPercent.Value.ToString("F1", culture)}%" : "--%";
        var cpuPeak = stats.PeakCpuPercent.HasValue ? $"{stats.PeakCpuPercent.Value.ToString("F1", culture)}%" : "--%";
        PerfCpuStatsText.Text = PerformanceSnapshotFormatter.FormatAvgPeak(cpuAvg, cpuPeak, loc);

        var ramAvg = FormatMegabytes(stats.AverageWorkingSetBytes);
        var ramPeak = FormatMegabytes(stats.PeakWorkingSetBytes);
        PerfRamStatsText.Text = PerformanceSnapshotFormatter.FormatAvgPeak(ramAvg, ramPeak, loc);

        var privAvg = FormatMegabytes(stats.AveragePrivateMemoryBytes);
        var privPeak = FormatMegabytes(stats.PeakPrivateMemoryBytes);
        PerfPrivateStatsText.Text = PerformanceSnapshotFormatter.FormatAvgPeak(privAvg, privPeak, loc);

        PerfThreadPeakText.Text = PerformanceSnapshotFormatter.FormatPeak(stats.PeakThreadCount, loc);
        PerfHandlePeakText.Text = PerformanceSnapshotFormatter.FormatPeak(stats.PeakHandleCount, loc);

        PerfWallpaperTypeStateText.Text = PerformanceSnapshotFormatter.FormatWallpaperTypeState(snap.WallpaperType, snap.WallpaperState, loc);
        PerfRendererStatusText.Text = PerformanceSnapshotFormatter.FormatRendererStatus(snap.RendererWindowAlive, loc);

        if (snap.IsImageActive)
        {
            PerfSourceResolutionText.Text = FormatDimensions(snap.SourceWidth, snap.SourceHeight);
            var decRes = FormatDimensions(snap.DecodeWidth, snap.DecodeHeight);
            var decMem = snap.ApproximateDecodedMemoryBytes.HasValue ? $" ({FormatMegabytes(snap.ApproximateDecodedMemoryBytes.Value)})" : "";
            PerfDecodeResolutionText.Text = $"{decRes}{decMem}";
            PerfMediaDetailsText.Text = isTr ? $"Yol: {snap.ActiveWallpaperPath ?? "(Yok)"}" : $"Path: {snap.ActiveWallpaperPath ?? "(None)"}";
        }
        else if (snap.IsVideoActive)
        {
            PerfSourceResolutionText.Text = FormatDimensions(snap.VideoWidth, snap.VideoHeight);
            PerfDecodeResolutionText.Text = FormatDimensions(snap.DecodeWidth, snap.DecodeHeight);
            var pos = PerformanceSnapshotFormatter.FormatTimeSpan(snap.PlaybackPosition);
            var dur = PerformanceSnapshotFormatter.FormatTimeSpan(snap.PlaybackDuration);
            var pStatus = snap.PlaybackStatus switch
            {
                "Playing" => isTr ? "Oynatılıyor" : "Playing",
                "Paused" => isTr ? "Duraklatıldı" : "Paused",
                _ => snap.PlaybackStatus ?? (isTr ? "Bilinmiyor" : "Unknown")
            };
            PerfMediaDetailsText.Text = isTr
                ? $"Durum: {pStatus} • Konum: {pos} / {dur} • Yol: {snap.ActiveWallpaperPath ?? "(Yok)"}"
                : $"Status: {pStatus} • Pos: {pos} / {dur} • Path: {snap.ActiveWallpaperPath ?? "(None)"}";
        }
        else
        {
            PerfSourceResolutionText.Text = isTr ? "Yok" : "None";
            PerfDecodeResolutionText.Text = isTr ? "Yok" : "None";
            PerfMediaDetailsText.Text = isTr ? "Aktif duvar kağıdı yok." : "No active wallpaper.";
        }

        PerfDesktopHostStateText.Text = PerformanceSnapshotFormatter.FormatDesktopHostState(snap.DesktopHostConnected, loc);
        PerfPauseReasonsText.Text = PerformanceSnapshotFormatter.FormatPauseReasons(snap.PauseReasons, loc);
        PerfFullscreenPolicyText.Text = PerformanceSnapshotFormatter.FormatFullscreenPolicy(snap.FullscreenPauseActive, loc);
        PerfPowerPolicyText.Text = PerformanceSnapshotFormatter.FormatPowerPolicy(snap.PowerPauseActive, loc);
        PerfSessionLockPolicyText.Text = PerformanceSnapshotFormatter.FormatSessionLockPolicy(snap.SessionLockedPauseActive, loc);
        PerfDisplayPowerPolicyText.Text = PerformanceSnapshotFormatter.FormatDisplayPowerPolicy(snap.DisplayOffPauseActive, loc);

        if (PerfResourceGuardText != null)
        {
            PerfResourceGuardText.Text = snap.ResourceGuardActive
                ? (isTr ? "Etkin (Yüksek yük nedeniyle kısıtlandı)" : "Active (Throttling video resources)")
                : (isTr ? "Etkin Değil (Normal yük)" : "Inactive (Normal load)");
        }

        if (PerfDecoderStateText != null)
        {
            var isFrozen = snap.WallpaperState == IpcConstants.PlaybackStateDeepFrozen ||
                           snap.WallpaperState == "DeepFrozen";
            PerfDecoderStateText.Text = isFrozen
                ? (isTr ? "Derin Donduruldu (Donanım bırakıldı)" : "Deep Frozen (Hardware released)")
                : (isTr ? "Aktif" : "Active");
        }

        if (PerfResourceBudgetText != null)
        {
            var b = trayIcon?.CurrentBudget ?? NiceWP.Core.ResourceBudget.Automatic;
            PerfResourceBudgetText.Text = b switch
            {
                NiceWP.Core.ResourceBudget.Low => isTr ? "Düşük (Tasarruf)" : "Low (Aggressive savings)",
                NiceWP.Core.ResourceBudget.Balanced => isTr ? "Dengeli" : "Balanced",
                NiceWP.Core.ResourceBudget.High => isTr ? "Yüksek Performans" : "High Performance",
                _ => isTr ? "Otomatik" : "Automatic"
            };
        }

        if (PerfLastActionText != null)
        {
            if (snap.LastAutomaticAction != null)
            {
                PerfLastActionText.Text = $"{snap.LastAutomaticAction.ActionType}: {snap.LastAutomaticAction.Reason}";
            }
            else
            {
                PerfLastActionText.Text = isTr ? "Otomatik işlem yapılmadı" : "No automatic actions";
            }
        }

        PerfEfficiencyProfileText.Text = PerformanceSnapshotFormatter.FormatEfficiencyProfile(snap.EfficiencyProfile, loc);
        PerfRuntimeHealthText.Text = PerformanceSnapshotFormatter.FormatHealth(snap.RuntimeHealth, loc);
        PerfRuntimeHealthText.Style = (Style)Application.Current.Resources[
            snap.RuntimeHealth == "Healthy" ? "HealthHealthyStyle"
            : snap.RuntimeHealth == "Degraded" ? "HealthWarningStyle"
            : snap.RuntimeHealth == "SafeStopped" ? "HealthAttentionStyle" : "HealthNeutralStyle"];
        PerfCurrentActionText.Text = PerformanceSnapshotFormatter.FormatCurrentAction(snap.CurrentAction, loc);

        if (snap.RecentEvents != null && snap.RecentEvents.Count > 0)
        {
            PerfRecentEventsCountText.Text = PerformanceSnapshotFormatter.FormatRecentEventsCount(Math.Min(6, snap.RecentEvents.Count), snap.RecentEvents.Count, loc);
            var sb = new StringBuilder();
            foreach (var evt in snap.RecentEvents.OrderByDescending(item => item.TimestampUtc).Take(6))
            {
                sb.AppendLine($"{evt.EventType} · {evt.TimestampUtc.ToLocalTime():HH:mm:ss}\n{evt.Message}\n");
            }
            PerfRecentEventsListText.Text = sb.ToString().TrimEnd();
        }
        else
        {
            PerfRecentEventsCountText.Text = isTr ? "0 olay" : "0 events";
            PerfRecentEventsListText.Text = isTr ? "Kayıtlı olay yok." : "No events recorded.";
        }

        PerfEfficiencyStatusText.Text = PerformanceSnapshotFormatter.FormatEfficiencyStatus(snap, loc);

        if (snap.GpuPercent.HasValue)
        {
            PerfGpuStatusText.Text = isTr
                ? $"GPU Kullanımı: {snap.GpuPercent.Value.ToString("F1", culture)}% • Ayrılmış Bellek: {FormatMegabytes(snap.GpuMemoryBytes ?? 0)}"
                : $"GPU Utilization: {snap.GpuPercent.Value.ToString("F1", culture)}% • Dedicated Memory: {FormatMegabytes(snap.GpuMemoryBytes ?? 0)}";
        }
        else
        {
            PerfGpuStatusText.Text = isTr
                ? "GPU metrikleri: Kullanılamıyor. Windows süreç düzeyinde GPU sayaçları tüm süreçler genelinde D3D motoru sayımı gerektirir ve örnek başına 2+ saniye ek yük getirir."
                : (snap.GpuStatusMessage ?? "GPU metrics: Unavailable. Windows process-level GPU counters require full D3D engine enumeration across all processes, adding 2+ seconds overhead per sample.");
        }
    }

    private void RenderPerformanceUnavailable(string reason)
    {
        var loc = LocalizationService.Instance;
        var isTr = loc.CurrentLanguage == EffectiveLanguage.Turkish;

        PerformanceHostStatusTitle.Text = PerformanceSnapshotFormatter.FormatHostStatusTitle(isConnected: false, isUnavailable: true, loc);
        PerformanceHostStatusDetails.Text = isTr ? LocalizeStatusMessage(reason) : reason;
        PerformanceLiveIndicator.Visibility = Visibility.Collapsed;

        PerfCpuCurrentText.Text = "--%";
        PerfWorkingSetText.Text = "-- MB";
        PerfPrivateMemoryText.Text = "-- MB";
        PerfThreadsText.Text = "--";
        PerfHandlesText.Text = "--";
        PerfUptimeText.Text = "--";

        PerfSampleCountText.Text = isTr ? "0 örnek" : "0 samples";
        PerfCpuStatsText.Text = PerformanceSnapshotFormatter.FormatAvgPeak("--%", "--%", loc);
        PerfRamStatsText.Text = PerformanceSnapshotFormatter.FormatAvgPeak("-- MB", "-- MB", loc);
        PerfPrivateStatsText.Text = PerformanceSnapshotFormatter.FormatAvgPeak("-- MB", "-- MB", loc);
        PerfThreadPeakText.Text = PerformanceSnapshotFormatter.FormatPeak("--", loc);
        PerfHandlePeakText.Text = PerformanceSnapshotFormatter.FormatPeak("--", loc);

        PerfWallpaperTypeStateText.Text = isTr ? "Bilinmiyor" : "Unknown";
        PerfRendererStatusText.Text = isTr ? "Etkin Değil" : "Inactive";
        PerfSourceResolutionText.Text = isTr ? "Kullanılamıyor" : "Unavailable";
        PerfDecodeResolutionText.Text = isTr ? "Kullanılamıyor" : "Unavailable";
        PerfMediaDetailsText.Text = isTr ? "Çalışma motoru yanıt vermiyor." : "Runtime host is not responding.";

        PerfDesktopHostStateText.Text = isTr ? "Bağlantı Yok" : "Disconnected";
        PerfPauseReasonsText.Text = isTr ? "Bilinmiyor" : "Unknown";
        PerfFullscreenPolicyText.Text = isTr ? "Kullanılamıyor" : "Unavailable";
        PerfPowerPolicyText.Text = isTr ? "Kullanılamıyor" : "Unavailable";
        PerfSessionLockPolicyText.Text = isTr ? "Kullanılamıyor" : "Unavailable";
        PerfDisplayPowerPolicyText.Text = isTr ? "Kullanılamıyor" : "Unavailable";
        PerfEfficiencyStatusText.Text = isTr ? "Verimlilik: Kullanılamıyor" : "Efficiency: Unavailable";

        PerfEfficiencyProfileText.Text = isTr ? "Bilinmiyor" : "Unknown";
        PerfRuntimeHealthText.Text = isTr ? "Kullanılamıyor" : "Unavailable";
        PerfRuntimeHealthText.Style = (Style)Application.Current.Resources["HealthNeutralStyle"];
        PerfCurrentActionText.Text = isTr ? "Çalışma motoru bağlantısı yok" : "Runtime host disconnected";
        PerfRecentEventsCountText.Text = isTr ? "0 olay" : "0 events";
        PerfRecentEventsListText.Text = isTr ? "Kullanılamıyor." : "Unavailable.";

        if (PerfResourceGuardText != null) PerfResourceGuardText.Text = isTr ? "Kullanılamıyor" : "Unavailable";
        if (PerfDecoderStateText != null) PerfDecoderStateText.Text = isTr ? "Kullanılamıyor" : "Unavailable";
        if (PerfResourceBudgetText != null) PerfResourceBudgetText.Text = isTr ? "Kullanılamıyor" : "Unavailable";
        if (PerfLastActionText != null) PerfLastActionText.Text = isTr ? "Bağlantı kesildi" : "Disconnected";
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
        $"{(bytes / (1024.0 * 1024.0)).ToString("F1", LocalizationService.Instance.Culture)} MB";

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
            PopulateScheduleWallpaperComboBoxes();
            UpdateFeedbackVisuals();
        });
    }

    private void InitializeLanguage()
    {
        try
        {
            var path = SettingsService.GetDefaultSettingsPath();
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("language", out var langProp) ||
                    doc.RootElement.TryGetProperty("Language", out langProp))
                {
                    var pref = AppLanguageExtensions.ParseLanguage(langProp.GetString());
                    var effective = new LanguageResolver().Resolve(pref);
                    LocalizationService.Instance.SetLanguage(effective);
                    return;
                }
            }
        }
        catch
        {
            // Ignore - fallback to system detector
        }

        var systemEffective = new LanguageResolver().Resolve(AppLanguage.System);
        LocalizationService.Instance.SetLanguage(systemEffective);
    }

    private void UpdateNavigationLocalization()
    {
        var loc = LocalizationService.Instance;
        LibraryNavigationItem.Content = loc.GetString(LocalizationKeys.NavLibrary);
        ActiveNavigationItem.Content = loc.GetString(LocalizationKeys.NavActiveWallpaper);
        PerformanceNavigationItem.Content = loc.GetString(LocalizationKeys.NavPerformance);
        SettingsNavigationItem.Content = loc.GetString(LocalizationKeys.NavSettings);
        if (AppTaglineText != null)
        {
            AppTaglineText.Text = loc.CurrentLanguage == EffectiveLanguage.Turkish ? "MASAÜSTÜ DENETİMİ" : "DESKTOP CONTROL";
        }
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
            SelectModeButton.Content = (libraryController?.IsSelectionMode == true) ? loc.GetString(LocalizationKeys.LibDone) : loc.GetString(LocalizationKeys.LibSelect);
        }

        if (BulkSelectAllButton != null) BulkSelectAllButton.Content = loc.GetString(LocalizationKeys.LibSelectAll);
        if (BulkClearSelectionButton != null) BulkClearSelectionButton.Content = loc.GetString(LocalizationKeys.LibClear);
        if (BulkFavoriteButton != null) BulkFavoriteButton.Content = loc.GetString(LocalizationKeys.LibFavorite);
        if (BulkUnfavoriteButton != null) BulkUnfavoriteButton.Content = loc.GetString(LocalizationKeys.LibUnfavorite);
        if (BulkRemoveButton != null)
        {
            var selectedCount = libraryController?.SelectedCount ?? 0;
            var hasSelection = selectedCount > 0;
            BulkRemoveButton.Content = hasSelection ? $"{loc.GetString(LocalizationKeys.LibRemove)} ({selectedCount})" : loc.GetString(LocalizationKeys.LibRemove);
        }
        if (BulkDoneButton != null) BulkDoneButton.Content = loc.GetString(LocalizationKeys.LibDone);

        var isTr = loc.CurrentLanguage == EffectiveLanguage.Turkish;
        if (EmptyTitleText != null) EmptyTitleText.Text = loc.GetString(LocalizationKeys.ItemEmptyTitle);
        if (EmptySubtitleText != null) EmptySubtitleText.Text = loc.GetString(LocalizationKeys.ItemEmptySubtitle);

        if (DropOverlayTitleText != null) DropOverlayTitleText.Text = isTr ? "Duvar kağıtlarını buraya bırakın" : "Drop wallpapers here";
        if (DropOverlaySubtitleText != null) DropOverlaySubtitleText.Text = isTr ? "Resimler (.png, .jpg) ve videolar (.mp4) desteklenir" : "Images (.png, .jpg) and videos (.mp4) supported";

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
        var isTr = loc.CurrentLanguage == EffectiveLanguage.Turkish;
        if (ActiveWallpaperTitleText != null) ActiveWallpaperTitleText.Text = loc.GetString(LocalizationKeys.NavActiveWallpaper);
        if (PreviousWallpaperButton != null)
        {
            PreviousWallpaperButton.Content = loc.GetString(LocalizationKeys.ActivePrevious);
            AutomationProperties.SetName(PreviousWallpaperButton, loc.GetString(LocalizationKeys.ActivePrevious));
            ToolTipService.SetToolTip(PreviousWallpaperButton, loc.GetString(LocalizationKeys.ActivePrevious));
        }
        if (NextWallpaperButton != null)
        {
            NextWallpaperButton.Content = loc.GetString(LocalizationKeys.ActiveNext);
            AutomationProperties.SetName(NextWallpaperButton, loc.GetString(LocalizationKeys.ActiveNext));
            ToolTipService.SetToolTip(NextWallpaperButton, loc.GetString(LocalizationKeys.ActiveNext));
        }
        if (StopActiveWallpaperButton != null)
        {
            StopActiveWallpaperButton.Content = loc.GetString(LocalizationKeys.ActiveStop);
            AutomationProperties.SetName(StopActiveWallpaperButton, loc.GetString(LocalizationKeys.ActiveStop));
        }
        if (SelectImageButton != null) SelectImageButton.Content = isTr ? "Dosya Seç…" : "Choose File…";
        if (RefreshActiveStatusButton != null) RefreshActiveStatusButton.Content = isTr ? "Yenile" : "Refresh";

        if (ActiveEmptyTitleText != null) ActiveEmptyTitleText.Text = isTr ? "Etkin duvar kağıdı yok" : "No wallpaper active";
        if (ActiveEmptySubtitleText != null) ActiveEmptySubtitleText.Text = isTr ? "Kütüphaneden bir duvar kağıdı uygulayın veya aşağıdan bir dosya seçin." : "Apply a wallpaper from Library or choose a file below.";
        if (ActiveSourceResolutionHeader != null) ActiveSourceResolutionHeader.Text = isTr ? "Kaynak çözünürlüğü · son ölçüm" : "Source resolution · last measurement";
        if (ActiveMissingWarningText != null) ActiveMissingWarningText.Text = isTr ? "Duvar kağıdı dosyası bulunamadı." : "The wallpaper file is missing.";

        UpdateActiveDetails(activeWallpaperPaused, activePlaybackState, statusKnown: true);
        UpdateRotationDetailsUI();
        UpdateScheduleDetailsUI();
    }
    private void OnScheduleStatusChanged(object? sender, WallpaperScheduleStatus status)
    {
        if (closed) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateScheduleDetailsUI(status);
            UpdateScheduleSettingsStatus(status);
        });
    }

    private void UpdateScheduleDetailsUI(WallpaperScheduleStatus? status = null)
    {
        if (ActiveScheduleStateText == null || closed) return;
        var loc = LocalizationService.Instance;
        var isTr = loc.CurrentLanguage == EffectiveLanguage.Turkish;
        status ??= libraryController?.Schedule?.GetStatus();
        if (status == null || !status.IsActive)
        {
            var offLabel = isTr ? "Kapalı" : "Off";
            ActiveScheduleStateText.Text = $"{loc.GetString(LocalizationKeys.ActiveSchedule)}: {offLabel}";
            return;
        }

        var slotLabel = status.CurrentSlot switch
        {
            DayNightSlot.Day => loc.GetString(LocalizationKeys.ActiveScheduleDay),
            DayNightSlot.Night => loc.GetString(LocalizationKeys.ActiveScheduleNight),
            _ => isTr ? "Aktif" : "Active"
        };

        if (status.IsOverdue)
        {
            var overdue = isTr ? "Gecikmiş (duraklatıldı)" : "Overdue (paused)";
            ActiveScheduleStateText.Text = $"{loc.GetString(LocalizationKeys.ActiveSchedule)}: {slotLabel} • {overdue}";
            return;
        }

        if (status.NextTransitionUtc.HasValue)
        {
            var nextLocal = status.NextTransitionUtc.Value.ToLocalTime();
            var nextSlotLabel = status.NextSlot switch
            {
                DayNightSlot.Day => loc.GetString(LocalizationKeys.ActiveScheduleDay),
                DayNightSlot.Night => loc.GetString(LocalizationKeys.ActiveScheduleNight),
                _ => string.Empty
            };
            var timeStr = nextLocal.ToString("HH:mm");
            var nextFormat = isTr
                ? $"Sonraki: {nextSlotLabel} {timeStr}"
                : $"Next: {nextSlotLabel} at {timeStr}";
            ActiveScheduleStateText.Text = $"{loc.GetString(LocalizationKeys.ActiveSchedule)}: {slotLabel} • {nextFormat}";
        }
        else
        {
            ActiveScheduleStateText.Text = $"{loc.GetString(LocalizationKeys.ActiveSchedule)}: {slotLabel}";
        }
    }

    private void UpdateScheduleSettingsStatus(WallpaperScheduleStatus? status = null)
    {
        if (ScheduleSettingsStatusText == null || closed) return;
        var loc = LocalizationService.Instance;
        var isTr = loc.CurrentLanguage == EffectiveLanguage.Turkish;
        status ??= libraryController?.Schedule?.GetStatus();
        if (status == null || !status.IsActive)
        {
            ScheduleSettingsStatusText.Text = isTr ? "Program devre dışı." : "Schedule is disabled.";
            if (ScheduleDayStartPanel != null) ScheduleDayStartPanel.Opacity = 0.5;
            if (ScheduleNightStartPanel != null) ScheduleNightStartPanel.Opacity = 0.5;
            if (ScheduleWallpapersGrid != null) ScheduleWallpapersGrid.Opacity = 0.5;
            return;
        }

        if (ScheduleDayStartPanel != null) ScheduleDayStartPanel.Opacity = 1.0;
        if (ScheduleNightStartPanel != null) ScheduleNightStartPanel.Opacity = 1.0;
        if (ScheduleWallpapersGrid != null) ScheduleWallpapersGrid.Opacity = 1.0;

        var dayName = !string.IsNullOrEmpty(status.CurrentWallpaperId) && libraryController?.Items?.FirstOrDefault(x => string.Equals(x.Id, status.CurrentWallpaperId, StringComparison.OrdinalIgnoreCase)) is { } item
            ? item.DisplayName : (isTr ? "Seçilmedi" : "None");
        ScheduleSettingsStatusText.Text = isTr
            ? $"Aktif: Gündüz / Gece Programı • Şu anki: {dayName}"
            : $"Active: Day / Night Schedule • Current: {dayName}";
    }

    private void PopulateScheduleWallpaperComboBoxes()
    {
        if (ScheduleDayWallpaperComboBox == null || ScheduleNightWallpaperComboBox == null) return;
        var items = libraryController?.Items ?? (IReadOnlyList<WallpaperLibraryEntryView>)Array.Empty<WallpaperLibraryEntryView>();
        var isTr = LocalizationService.Instance.CurrentLanguage == EffectiveLanguage.Turkish;
        var noneLabel = isTr ? "(Seçilmedi)" : "(None selected)";

        var selectedDayTag = (ScheduleDayWallpaperComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        var selectedNightTag = (ScheduleNightWallpaperComboBox.SelectedItem as ComboBoxItem)?.Tag as string;

        ScheduleDayWallpaperComboBox.Items.Clear();
        ScheduleNightWallpaperComboBox.Items.Clear();

        ScheduleDayWallpaperComboBox.Items.Add(new ComboBoxItem { Content = noneLabel, Tag = "" });
        ScheduleNightWallpaperComboBox.Items.Add(new ComboBoxItem { Content = noneLabel, Tag = "" });

        foreach (var it in items)
        {
            ScheduleDayWallpaperComboBox.Items.Add(new ComboBoxItem { Content = it.DisplayName, Tag = it.Id });
            ScheduleNightWallpaperComboBox.Items.Add(new ComboBoxItem { Content = it.DisplayName, Tag = it.Id });
        }

        if (PresentationWallpaperComboBox != null)
        {
            var selectedPresTag = (PresentationWallpaperComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
            PresentationWallpaperComboBox.Items.Clear();
            PresentationWallpaperComboBox.Items.Add(new ComboBoxItem { Content = noneLabel, Tag = "" });
            foreach (var it in items)
            {
                PresentationWallpaperComboBox.Items.Add(new ComboBoxItem { Content = it.DisplayName, Tag = it.Id });
            }
            SelectComboBoxByTag(PresentationWallpaperComboBox, selectedPresTag);
        }

        // Restore selections
        SelectComboBoxByTag(ScheduleDayWallpaperComboBox, selectedDayTag ?? libraryController?.Schedule?.DayWallpaperId);
        SelectComboBoxByTag(ScheduleNightWallpaperComboBox, selectedNightTag ?? libraryController?.Schedule?.NightWallpaperId);
    }

    private static void SelectComboBoxByTag(ComboBox? cb, string? tag)
    {
        if (cb == null) return;
        var target = tag ?? "";
        for (var i = 0; i < cb.Items.Count; i++)
        {
            if (cb.Items[i] is ComboBoxItem item && string.Equals(item.Tag as string ?? "", target, StringComparison.OrdinalIgnoreCase))
            {
                cb.SelectedIndex = i;
                return;
            }
        }
        cb.SelectedIndex = 0;
    }

    private void SelectScheduleSettingsItems(AppSettings settings)
    {
        SelectComboBoxByTag(ScheduleModeComboBox, settings.EffectiveScheduleMode);
        SelectComboBoxByTag(ScheduleDayStartComboBox, settings.EffectiveDayStartTime);
        SelectComboBoxByTag(ScheduleNightStartComboBox, settings.EffectiveNightStartTime);
        PopulateScheduleWallpaperComboBoxes();
        SelectComboBoxByTag(ScheduleDayWallpaperComboBox, settings.DayWallpaperId);
        SelectComboBoxByTag(ScheduleNightWallpaperComboBox, settings.NightWallpaperId);
        SelectComboBoxByTag(PresentationWallpaperComboBox, settings.PresentationWallpaperId);
        SelectComboBoxByTag(AdaptiveProfileComboBox, settings.EffectiveAdaptiveProfile.ToString());
        SelectComboBoxByTag(ResourceBudgetComboBox, settings.EffectiveResourceBudget.ToString());
        trayIcon.CurrentProfile = settings.EffectiveAdaptiveProfile;
        trayIcon.CurrentBudget = settings.EffectiveResourceBudget;
        UpdateScheduleSettingsStatus();
        UpdateAdaptiveSettingsStatus();
    }

    private async void OnAdaptiveProfileSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (isSyncingAppSettings || closed) return;
        var tag = (AdaptiveProfileComboBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "Automatic";
        var parsed = Enum.TryParse<AdaptiveDesktopProfile>(tag, true, out var p) ? p : AdaptiveDesktopProfile.Automatic;

        try
        {
            var cur = await settingsService.LoadAsync();
            cur.EffectiveAdaptiveProfile = parsed;
            await settingsService.SaveAsync(cur);
            trayIcon.CurrentProfile = parsed;
            UpdateAdaptiveSettingsStatus();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error saving AdaptiveProfile: {ex}");
        }
    }

    private async void OnResourceBudgetSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (isSyncingAppSettings || closed) return;
        var tag = (ResourceBudgetComboBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "Automatic";
        var parsed = Enum.TryParse<ResourceBudget>(tag, true, out var b) ? b : ResourceBudget.Automatic;

        try
        {
            var cur = await settingsService.LoadAsync();
            cur.EffectiveResourceBudget = parsed;
            await settingsService.SaveAsync(cur);
            trayIcon.CurrentBudget = parsed;
            UpdateAdaptiveSettingsStatus();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error saving ResourceBudget: {ex}");
        }
    }

    private void UpdateAdaptiveSettingsStatus()
    {
        if (AdaptiveSettingsStatusText == null) return;
        var loc = LocalizationService.Instance;
        var isTr = loc.CurrentLanguage == EffectiveLanguage.Turkish;

        var profileTag = (AdaptiveProfileComboBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "Automatic";
        var budgetTag = (ResourceBudgetComboBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "Automatic";

        AdaptiveSettingsStatusText.Text = isTr
            ? $"Etkin yapılandırma: Profil = {profileTag}, Kaynak Bütçesi = {budgetTag}. Arka plan motoru bu kurallara göre uyarlanır."
            : $"Active configuration: Profile = {profileTag}, Budget = {budgetTag}. Background engine adapts according to these rules.";
    }

    private async void OnPresentationWallpaperSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (isSyncingAppSettings || closed) return;
        var tag = (PresentationWallpaperComboBox?.SelectedItem as ComboBoxItem)?.Tag as string;
        var presWall = string.IsNullOrWhiteSpace(tag) ? null : tag;

        try
        {
            var cur = await settingsService.LoadAsync();
            cur.PresentationWallpaperId = presWall;
            await settingsService.SaveAsync(cur);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error saving PresentationWallpaperId: {ex}");
        }
    }

    private async void OnScheduleModeSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (isSyncingAppSettings || closed || libraryController == null) return;
        var tag = (ScheduleModeComboBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "Off";
        var parsed = ScheduleModeExtensions.ParseMode(tag);
        var dayStart = TimeOnly.TryParse((ScheduleDayStartComboBox?.SelectedItem as ComboBoxItem)?.Tag as string, out var ds) ? ds : new TimeOnly(8, 0);
        var nightStart = TimeOnly.TryParse((ScheduleNightStartComboBox?.SelectedItem as ComboBoxItem)?.Tag as string, out var ns) ? ns : new TimeOnly(20, 0);
        var dayWall = (ScheduleDayWallpaperComboBox?.SelectedItem as ComboBoxItem)?.Tag as string;
        var nightWall = (ScheduleNightWallpaperComboBox?.SelectedItem as ComboBoxItem)?.Tag as string;

        libraryController.Schedule.Configure(parsed, dayStart, nightStart, dayWall, nightWall);
        UpdateScheduleSettingsStatus();
        UpdateScheduleDetailsUI();

        try
        {
            var cur = await settingsService.LoadAsync();
            cur.ScheduleMode = tag == "Off" ? null : tag;
            await settingsService.SaveAsync(cur);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error saving ScheduleMode: {ex}");
        }
    }

    private async void OnScheduleDayStartSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (isSyncingAppSettings || closed || libraryController == null) return;
        var tag = (ScheduleDayStartComboBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "08:00";
        var dayStart = TimeOnly.TryParse(tag, out var ds) ? ds : new TimeOnly(8, 0);
        var nightStart = TimeOnly.TryParse((ScheduleNightStartComboBox?.SelectedItem as ComboBoxItem)?.Tag as string, out var ns) ? ns : new TimeOnly(20, 0);

        libraryController.Schedule.Configure(libraryController.Schedule.Mode, dayStart, nightStart, libraryController.Schedule.DayWallpaperId, libraryController.Schedule.NightWallpaperId);
        UpdateScheduleSettingsStatus();

        try
        {
            var cur = await settingsService.LoadAsync();
            cur.DayStartTime = tag == "08:00" ? null : tag;
            await settingsService.SaveAsync(cur);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error saving DayStartTime: {ex}");
        }
    }

    private async void OnScheduleNightStartSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (isSyncingAppSettings || closed || libraryController == null) return;
        var tag = (ScheduleNightStartComboBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "20:00";
        var nightStart = TimeOnly.TryParse(tag, out var ns) ? ns : new TimeOnly(20, 0);
        var dayStart = TimeOnly.TryParse((ScheduleDayStartComboBox?.SelectedItem as ComboBoxItem)?.Tag as string, out var ds) ? ds : new TimeOnly(8, 0);

        libraryController.Schedule.Configure(libraryController.Schedule.Mode, dayStart, nightStart, libraryController.Schedule.DayWallpaperId, libraryController.Schedule.NightWallpaperId);
        UpdateScheduleSettingsStatus();

        try
        {
            var cur = await settingsService.LoadAsync();
            cur.NightStartTime = tag == "20:00" ? null : tag;
            await settingsService.SaveAsync(cur);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error saving NightStartTime: {ex}");
        }
    }

    private async void OnScheduleDayWallpaperSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (isSyncingAppSettings || closed || libraryController == null) return;
        var tag = (ScheduleDayWallpaperComboBox?.SelectedItem as ComboBoxItem)?.Tag as string;
        var dayWall = string.IsNullOrEmpty(tag) ? null : tag;

        libraryController.Schedule.Configure(libraryController.Schedule.Mode, libraryController.Schedule.DayStartTime, libraryController.Schedule.NightStartTime, dayWall, libraryController.Schedule.NightWallpaperId);
        UpdateScheduleSettingsStatus();

        try
        {
            var cur = await settingsService.LoadAsync();
            cur.DayWallpaperId = dayWall;
            await settingsService.SaveAsync(cur);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error saving DayWallpaperId: {ex}");
        }
    }

    private async void OnScheduleNightWallpaperSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (isSyncingAppSettings || closed || libraryController == null) return;
        var tag = (ScheduleNightWallpaperComboBox?.SelectedItem as ComboBoxItem)?.Tag as string;
        var nightWall = string.IsNullOrEmpty(tag) ? null : tag;

        libraryController.Schedule.Configure(libraryController.Schedule.Mode, libraryController.Schedule.DayStartTime, libraryController.Schedule.NightStartTime, libraryController.Schedule.DayWallpaperId, nightWall);
        UpdateScheduleSettingsStatus();

        try
        {
            var cur = await settingsService.LoadAsync();
            cur.NightWallpaperId = nightWall;
            await settingsService.SaveAsync(cur);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error saving NightWallpaperId: {ex}");
        }
    }

    private void RenderAppRules()
    {
        if (AppRulesListContainer == null) return;
        AppRulesListContainer.Children.Clear();
        var rules = appRuleMatchingService.CurrentRules;
        var isTr = LocalizationService.Instance.CurrentLanguage == EffectiveLanguage.Turkish;

        if (rules.Count == 0)
        {
            var emptyText = new TextBlock
            {
                Text = isTr ? "Yapılandırılmış uygulama kuralı yok." : "No application rules configured.",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };
            AppRulesListContainer.Children.Add(emptyText);
            return;
        }

        foreach (var rule in rules)
        {
            var border = new Border
            {
                Background = (Brush)Application.Current.Resources["ControlFillColorDefaultBrush"],
                BorderBrush = (Brush)Application.Current.Resources["ControlStrokeBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var infoPanel = new StackPanel { Spacing = 2 };
            var procText = new TextBlock { Text = rule.ProcessName, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            var behaviorDesc = FormatAppRuleBehavior(rule.Behavior, isTr);
            var descText = new TextBlock
            {
                Text = behaviorDesc,
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };
            infoPanel.Children.Add(procText);
            infoPanel.Children.Add(descText);
            Grid.SetColumn(infoPanel, 0);
            grid.Children.Add(infoPanel);

            var editBtn = new Button
            {
                Content = isTr ? "Düzenle" : "Edit",
                Margin = new Thickness(8, 0, 4, 0),
                FontSize = 12
            };
            var currentRule = rule;
            editBtn.Click += async (_, _) => await ShowAppRuleEditDialogAsync(currentRule);
            Grid.SetColumn(editBtn, 1);
            grid.Children.Add(editBtn);

            var removeBtn = new Button
            {
                Content = isTr ? "Kaldır" : "Remove",
                Margin = new Thickness(4, 0, 0, 0),
                FontSize = 12
            };
            removeBtn.Click += async (_, _) => await RemoveAppRuleAsync(currentRule.ProcessName);
            Grid.SetColumn(removeBtn, 2);
            grid.Children.Add(removeBtn);

            border.Child = grid;
            AppRulesListContainer.Children.Add(border);
        }
    }

    private static string FormatAppRuleBehavior(AppRuleBehavior behavior, bool isTr) => behavior switch
    {
        AppRuleBehavior.Focus => isTr ? "Odak Profili: Video duraklatılır, resim korunur" : "Focus Profile: Pauses video, keeps images",
        AppRuleBehavior.Gaming => isTr ? "Oyun Profili: Video duraklatılır ve dondurulur" : "Gaming Profile: Pauses & deep freezes video",
        AppRuleBehavior.Presentation => isTr ? "Sunum Profili: Hareket durdurulur, sunum duvar kağıdı açılır" : "Presentation Profile: Halts motion, uses presentation wallpaper",
        AppRuleBehavior.Cinema => isTr ? "Sinema Profili: Video duraklatılır" : "Cinema Profile: Pauses video during media",
        AppRuleBehavior.Pause => isTr ? "Duraklat: Duvar kağıdını duraklat" : "Pause: Pauses wallpaper",
        AppRuleBehavior.DeepFreeze => isTr ? "Derin Dondur: Video kaynaklarını bırak" : "Deep Freeze: Releases video decoder resources",
        AppRuleBehavior.UseWallpaper => isTr ? "Duvar Kağıdı Uygula: Özel duvar kağıdına geç" : "Use Wallpaper: Switches to designated wallpaper",
        _ => isTr ? "İşlem Yok" : "No Action"
    };

    private async Task RemoveAppRuleAsync(string processName)
    {
        try
        {
            var cur = await settingsService.LoadAsync();
            cur.AppRules.RemoveAll(r => string.Equals(r.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
            await settingsService.SaveAsync(cur);
            appRuleMatchingService.UpdateRules(cur.AppRules);
            RenderAppRules();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error removing app rule: {ex}");
        }
    }

    private async void OnAddAppRuleClicked(object sender, RoutedEventArgs e)
    {
        await ShowAppRuleEditDialogAsync(null);
    }

    private async void OnAddFromRunningAppClicked(object sender, RoutedEventArgs e)
    {
        await ShowRunningAppPickerDialogAsync();
    }

    private async Task ShowAppRuleEditDialogAsync(AppRule? existingRule, string? initialProcessName = null)
    {
        var isTr = LocalizationService.Instance.CurrentLanguage == EffectiveLanguage.Turkish;
        var panel = new StackPanel { Spacing = 12, Width = 340 };

        var procBox = new TextBox
        {
            Header = isTr ? "Uygulama Adı (.exe)" : "Application Executable (.exe)",
            PlaceholderText = "ör. devenv.exe, powerpnt.exe",
            Text = existingRule?.ProcessName ?? initialProcessName ?? string.Empty
        };
        panel.Children.Add(procBox);

        var behaviorBox = new ComboBox
        {
            Header = isTr ? "Davranış / Profil" : "Behavior / Profile",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        behaviorBox.Items.Add(new ComboBoxItem { Content = isTr ? "Odak (Focus)" : "Focus", Tag = AppRuleBehavior.Focus });
        behaviorBox.Items.Add(new ComboBoxItem { Content = isTr ? "Oyun (Gaming)" : "Gaming", Tag = AppRuleBehavior.Gaming });
        behaviorBox.Items.Add(new ComboBoxItem { Content = isTr ? "Sunum (Presentation)" : "Presentation", Tag = AppRuleBehavior.Presentation });
        behaviorBox.Items.Add(new ComboBoxItem { Content = isTr ? "Sinema (Cinema)" : "Cinema", Tag = AppRuleBehavior.Cinema });
        behaviorBox.Items.Add(new ComboBoxItem { Content = isTr ? "Duraklat (Pause)" : "Pause", Tag = AppRuleBehavior.Pause });
        behaviorBox.Items.Add(new ComboBoxItem { Content = isTr ? "Derin Dondur (Deep Freeze)" : "Deep Freeze", Tag = AppRuleBehavior.DeepFreeze });

        var initialBehavior = existingRule?.Behavior ?? AppRuleBehavior.Focus;
        var matchingItem = behaviorBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (AppRuleBehavior)i.Tag == initialBehavior);
        behaviorBox.SelectedItem = matchingItem ?? behaviorBox.Items[0];
        panel.Children.Add(behaviorBox);

        var dialog = new ContentDialog
        {
            XamlRoot = this.Content.XamlRoot,
            Title = existingRule != null
                ? (isTr ? "Uygulama Kuralını Düzenle" : "Edit Application Rule")
                : (isTr ? "Uygulama Kuralı Ekle" : "Add Application Rule"),
            PrimaryButtonText = isTr ? "Kaydet" : "Save",
            CloseButtonText = isTr ? "İptal" : "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = panel
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var rawProc = procBox.Text?.Trim();
            var normalizedProc = AppRuleNormalizer.NormalizeProcessName(rawProc);
            if (string.IsNullOrEmpty(normalizedProc)) return;

            var chosenBehavior = (AppRuleBehavior)((ComboBoxItem)behaviorBox.SelectedItem).Tag;

            try
            {
                var cur = await settingsService.LoadAsync();
                cur.AppRules.RemoveAll(r => string.Equals(r.ProcessName, normalizedProc, StringComparison.OrdinalIgnoreCase));
                if (existingRule != null && !string.Equals(existingRule.ProcessName, normalizedProc, StringComparison.OrdinalIgnoreCase))
                {
                    cur.AppRules.RemoveAll(r => string.Equals(r.ProcessName, existingRule.ProcessName, StringComparison.OrdinalIgnoreCase));
                }

                cur.AppRules.Add(new AppRule
                {
                    ProcessName = normalizedProc,
                    Behavior = chosenBehavior,
                    OptionalWallpaperId = existingRule?.OptionalWallpaperId
                });

                await settingsService.SaveAsync(cur);
                appRuleMatchingService.UpdateRules(cur.AppRules);
                RenderAppRules();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Error saving app rule: {ex}");
            }
        }
    }

    private async Task ShowRunningAppPickerDialogAsync()
    {
        var isTr = LocalizationService.Instance.CurrentLanguage == EffectiveLanguage.Turkish;
        var apps = RunningApplicationPickerService.GetRunningApplications();

        var panel = new StackPanel { Spacing = 12, Width = 340 };
        var infoText = new TextBlock
        {
            Text = isTr
                ? "Şu anda çalışan uygulamalardan birini seçin:"
                : "Select from currently running applications:",
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        panel.Children.Add(infoText);

        var appList = new ComboBox
        {
            Header = isTr ? "Çalışan Uygulamalar" : "Running Applications",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        foreach (var app in apps)
        {
            appList.Items.Add(new ComboBoxItem
            {
                Content = $"{app.DisplayName} ({app.ProcessName})",
                Tag = app.ProcessName
            });
        }

        if (appList.Items.Count > 0)
        {
            appList.SelectedIndex = 0;
        }
        else
        {
            appList.Items.Add(new ComboBoxItem
            {
                Content = isTr ? "Çalışan uygun uygulama bulunamadı" : "No suitable running applications found",
                Tag = null,
                IsEnabled = false
            });
            appList.SelectedIndex = 0;
        }

        panel.Children.Add(appList);

        var dialog = new ContentDialog
        {
            XamlRoot = this.Content.XamlRoot,
            Title = isTr ? "Çalışan Uygulamadan Kural Oluştur" : "Create Rule from Running Application",
            PrimaryButtonText = isTr ? "Seç" : "Select",
            CloseButtonText = isTr ? "İptal" : "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = panel
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && appList.SelectedItem is ComboBoxItem sel && sel.Tag is string procName)
        {
            await ShowAppRuleEditDialogAsync(null, procName);
        }
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

        if (SettingsScheduleHeader != null) SettingsScheduleHeader.Text = loc.GetString(LocalizationKeys.SetWallpaperSchedule);
        if (SettingsScheduleSubtitle != null) SettingsScheduleSubtitle.Text = isTr ? "Gündüz ve gece duvar kağıtları arasında otomatik geçiş yapın." : "Automatically transition between Day and Night wallpapers.";
        if (SettingsScheduleModeLabel != null) SettingsScheduleModeLabel.Text = loc.GetString(LocalizationKeys.SetScheduleMode);
        if (SettingsDayStartLabel != null) SettingsDayStartLabel.Text = isTr ? "Gündüz başlangıcı" : "Day begins";
        if (SettingsNightStartLabel != null) SettingsNightStartLabel.Text = isTr ? "Gece başlangıcı" : "Night begins";
        if (SettingsDayWallpaperLabel != null) SettingsDayWallpaperLabel.Text = loc.GetString(LocalizationKeys.SetDayWallpaper);
        if (SettingsNightWallpaperLabel != null) SettingsNightWallpaperLabel.Text = loc.GetString(LocalizationKeys.SetNightWallpaper);

        if (ScheduleModeComboBox != null)
        {
            foreach (var item in ScheduleModeComboBox.Items.OfType<ComboBoxItem>())
            {
                var tag = item.Tag as string;
                item.Content = tag switch
                {
                    "Off" => isTr ? "Kapalı" : "Off",
                    "DayNight" => isTr ? "Gündüz ve Gece" : "Day & Night",
                    _ => item.Content
                };
            }
        }

        UpdateScheduleSettingsStatus();

        if (SettingsPresentationWallpaperHeader != null) SettingsPresentationWallpaperHeader.Text = isTr ? "Sunum Profili Duvar Kağıdı (İsteğe Bağlı)" : "Presentation Profile Wallpaper (Optional)";
        if (SettingsPresentationWallpaperSubtitle != null) SettingsPresentationWallpaperSubtitle.Text = isTr
            ? "Toplantı veya ekran paylaşımı oturumlarında gösterilecek duvar kağıdı."
            : "Wallpaper to show during meetings or screen share sessions.";
        if (SettingsAppRulesHeader != null) SettingsAppRulesHeader.Text = isTr ? "Uygulama Kuralları" : "Application Rules";
        if (SettingsAppRulesSubtitle != null) SettingsAppRulesSubtitle.Text = isTr
            ? "Belirli uygulamalar aktif olduğunda atanacak masaüstü profilini veya davranışını belirleyin."
            : "Assign specific desktop profiles or behaviors when selected applications are active.";
        if (AddAppRuleButton != null) AddAppRuleButton.Content = isTr ? "Kural Ekle" : "Add Rule";
        if (AddFromRunningAppButton != null) AddFromRunningAppButton.Content = isTr ? "Çalışan Uygulamalardan…" : "Add from Running…";
        RenderAppRules();

        if (SettingsStorageHeader != null) SettingsStorageHeader.Text = isTr ? "Sistem Tepsisi ve Yerel Depolama" : "System Tray & Local Storage";
        if (SettingsStorageSubtitle != null) SettingsStorageSubtitle.Text = isTr
            ? "Bu pencereyi kapattığınızda NiceWP sistem tepsisinde çalışmaya devam eder. Duvar kağıdınız aktif kalır."
            : "NiceWP stays in the system tray when you close this window. Your wallpaper keeps running.";

        if (SettingsAdaptiveHeader != null) SettingsAdaptiveHeader.Text = isTr ? "Adaptif Masaüstü Zekası" : "Adaptive Desktop Intelligence";
        if (SettingsAdaptiveSubtitle != null) SettingsAdaptiveSubtitle.Text = isTr ? "Otomatik davranış profillerini ve donanım kaynak sınırlarını yapılandırın." : "Configure automatic behavior profiles and hardware resource limits.";
        if (SettingsAdaptiveProfileLabel != null) SettingsAdaptiveProfileLabel.Text = isTr ? "Varsayılan Profil" : "Default Profile";
        if (SettingsResourceBudgetLabel != null) SettingsResourceBudgetLabel.Text = isTr ? "Kaynak Bütçesi" : "Resource Budget";

        if (AdaptiveProfileComboBox != null)
        {
            foreach (var item in AdaptiveProfileComboBox.Items.OfType<ComboBoxItem>())
            {
                var tag = item.Tag as string;
                item.Content = tag switch
                {
                    "Automatic" => isTr ? "Otomatik (Bağlama duyarlı)" : "Automatic (Context-aware)",
                    "Normal" => isTr ? "Normal" : "Normal",
                    "Focus" => isTr ? "Odak (Çalışma)" : "Focus",
                    "Gaming" => isTr ? "Oyun (Düşük gecikme)" : "Gaming",
                    "Presentation" => isTr ? "Sunum" : "Presentation",
                    "BatterySaver" => isTr ? "Pil Tasarrufu" : "Battery Saver",
                    "Cinema" => isTr ? "Sinema (Medya)" : "Cinema",
                    _ => item.Content
                };
            }
        }

        if (ResourceBudgetComboBox != null)
        {
            foreach (var item in ResourceBudgetComboBox.Items.OfType<ComboBoxItem>())
            {
                var tag = item.Tag as string;
                item.Content = tag switch
                {
                    "Automatic" => isTr ? "Otomatik (Dengeli / Güç duyarlı)" : "Automatic (Balanced / Power-aware)",
                    "Low" => isTr ? "Düşük (Agresif tasarruf)" : "Low (Aggressive savings)",
                    "Balanced" => isTr ? "Dengeli (Önerilen)" : "Balanced (Recommended)",
                    "High" => isTr ? "Yüksek (Yüksek performans)" : "High (High performance)",
                    _ => item.Content
                };
            }
        }

        UpdateAdaptiveSettingsStatus();

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
        if (PerfResourceGuardLabel != null) PerfResourceGuardLabel.Text = isTr ? "Kaynak Koruması" : "Resource Guard";
        if (PerfDecoderStateLabel != null) PerfDecoderStateLabel.Text = isTr ? "Donanım Çözücü Durumu" : "Hardware Decoder State";
        if (PerfResourceBudgetLabel != null) PerfResourceBudgetLabel.Text = isTr ? "Kaynak Bütçesi" : "Resource Budget";
        if (PerfLastActionLabel != null) PerfLastActionLabel.Text = isTr ? "Son Otomatik İşlem" : "Last Automatic Action";

        if (PerfAuditTrailHeader != null) PerfAuditTrailHeader.Text = isTr ? "Denetim Kaydı" : "Audit Trail";
        if (PerfGpuHeader != null) PerfGpuHeader.Text = isTr ? "GPU Ölçümü" : "GPU Measurement";
        if (PerfPrivacyHeader != null) PerfPrivacyHeader.Text = isTr ? "Gizlilik ve Yerel Şeffaflık" : "Privacy & Local Transparency";
        if (PerfPrivacySubtitle != null) PerfPrivacySubtitle.Text = isTr ? "Metrikler bu cihazda kalır. Hiçbir telemetri gönderilmez." : "Metrics stay on this device. No performance telemetry is sent.";

        if (PerformanceLiveIndicator != null) PerformanceLiveIndicator.Text = PerformanceSnapshotFormatter.FormatLiveIndicator(loc);

        if (latestPerformanceStatus != null)
        {
            RenderPerformanceStatus(latestPerformanceStatus);
        }
        else
        {
            RenderPerformanceUnavailable(isTr ? "Çalışma motoru bekleniyor." : "Waiting for runtime.");
        }
    }

    private static string FormatDimensions(uint? w, uint? h) =>
        w.HasValue && h.HasValue ? $"{w.Value} x {h.Value}" : (LocalizationService.Instance.CurrentLanguage == EffectiveLanguage.Turkish ? "Kullanılamıyor" : "Unavailable");

    private async void OnTrayProfileSelected(AdaptiveDesktopProfile profile)
    {
        try
        {
            var cur = await settingsService.LoadAsync();
            cur.EffectiveAdaptiveProfile = profile;
            await settingsService.SaveAsync(cur);
            trayIcon.CurrentProfile = profile;
            SelectComboBoxByTag(AdaptiveProfileComboBox, profile.ToString());
            StatusText.Text = $"Adaptive Profile: {profile}";
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error changing profile from tray: {ex}");
        }
    }

    private async void OnTrayBudgetSelected(ResourceBudget budget)
    {
        try
        {
            var cur = await settingsService.LoadAsync();
            cur.EffectiveResourceBudget = budget;
            await settingsService.SaveAsync(cur);
            trayIcon.CurrentBudget = budget;
            SelectComboBoxByTag(ResourceBudgetComboBox, budget.ToString());
            StatusText.Text = $"Resource Budget: {budget}";
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error changing budget from tray: {ex}");
        }
    }
}
