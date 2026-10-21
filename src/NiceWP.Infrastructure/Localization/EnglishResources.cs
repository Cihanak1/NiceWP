namespace NiceWP.Infrastructure.Localization;

/// <summary>
/// Registers the full English (en-US) localization dictionary.
/// </summary>
internal static class EnglishResources
{
    public static void Register(LocalizationService service)
    {
        // Navigation
        service.RegisterEnglish(LocalizationKeys.NavLibrary, "Library");
        service.RegisterEnglish(LocalizationKeys.NavActiveWallpaper, "Active Wallpaper");
        service.RegisterEnglish(LocalizationKeys.NavPerformance, "Performance");
        service.RegisterEnglish(LocalizationKeys.NavSettings, "Settings");

        // Library Controls
        service.RegisterEnglish(LocalizationKeys.LibAddWallpaper, "Add Wallpaper");
        service.RegisterEnglish(LocalizationKeys.LibAddFromUrl, "Add from URL");
        service.RegisterEnglish(LocalizationKeys.LibAddFolder, "Add Folder");
        service.RegisterEnglish(LocalizationKeys.LibSelect, "Select");
        service.RegisterEnglish(LocalizationKeys.LibSelectAll, "Select All");
        service.RegisterEnglish(LocalizationKeys.LibClear, "Clear");
        service.RegisterEnglish(LocalizationKeys.LibFavorite, "Favorite");
        service.RegisterEnglish(LocalizationKeys.LibUnfavorite, "Unfavorite");
        service.RegisterEnglish(LocalizationKeys.LibRemove, "Remove");
        service.RegisterEnglish(LocalizationKeys.LibDone, "Done");
        service.RegisterEnglish(LocalizationKeys.LibSearchPlaceholder, "Search wallpapers...");
        service.RegisterEnglish(LocalizationKeys.LibFilter, "Filter");
        service.RegisterEnglish(LocalizationKeys.LibSort, "Sort");

        // Library Filters & Sort
        service.RegisterEnglish(LocalizationKeys.FilterAll, "All");
        service.RegisterEnglish(LocalizationKeys.FilterImages, "Images");
        service.RegisterEnglish(LocalizationKeys.FilterVideos, "Videos");
        service.RegisterEnglish(LocalizationKeys.FilterFavorites, "Favorites");
        service.RegisterEnglish(LocalizationKeys.FilterRecent, "Recent");
        service.RegisterEnglish(LocalizationKeys.FilterMissing, "Missing");
        service.RegisterEnglish(LocalizationKeys.FilterRemote, "Remote");

        service.RegisterEnglish(LocalizationKeys.SortRecent, "Recently Added");
        service.RegisterEnglish(LocalizationKeys.SortName, "Name (A–Z)");
        service.RegisterEnglish(LocalizationKeys.SortType, "Type");
        service.RegisterEnglish(LocalizationKeys.SortFavorites, "Favorites First");

        // Library Context / Item Actions
        service.RegisterEnglish(LocalizationKeys.ItemActive, "ACTIVE");
        service.RegisterEnglish(LocalizationKeys.ItemApply, "Apply");
        service.RegisterEnglish(LocalizationKeys.ItemReapply, "Reapply");
        service.RegisterEnglish(LocalizationKeys.ItemRename, "Rename");
        service.RegisterEnglish(LocalizationKeys.ItemOpenFileLocation, "Open file location");
        service.RegisterEnglish(LocalizationKeys.ItemLocateFile, "Locate file...");
        service.RegisterEnglish(LocalizationKeys.ItemSelectedCount, "{0} selected");
        service.RegisterEnglish(LocalizationKeys.ItemEmptyTitle, "Your library is empty");
        service.RegisterEnglish(LocalizationKeys.ItemEmptySubtitle, "Add your first wallpaper from local files, folder, or remote URL.");

        // Active Wallpaper Controls & Status
        service.RegisterEnglish(LocalizationKeys.ActivePrevious, "Previous");
        service.RegisterEnglish(LocalizationKeys.ActivePause, "Pause");
        service.RegisterEnglish(LocalizationKeys.ActiveResume, "Resume");
        service.RegisterEnglish(LocalizationKeys.ActiveNext, "Next");
        service.RegisterEnglish(LocalizationKeys.ActiveStop, "Stop");
        service.RegisterEnglish(LocalizationKeys.ActiveRotation, "Rotation");
        service.RegisterEnglish(LocalizationKeys.ActivePosition, "Position");
        service.RegisterEnglish(LocalizationKeys.ActiveNextChange, "Next change");
        service.RegisterEnglish(LocalizationKeys.ActiveOverdue, "Overdue");
        service.RegisterEnglish(LocalizationKeys.ActiveType, "Type");
        service.RegisterEnglish(LocalizationKeys.ActiveState, "State");
        service.RegisterEnglish(LocalizationKeys.ActiveEfficiency, "Efficiency");
        service.RegisterEnglish(LocalizationKeys.ActiveImage, "Image");
        service.RegisterEnglish(LocalizationKeys.ActiveVideo, "Video");
        service.RegisterEnglish(LocalizationKeys.ActiveRunning, "Running");
        service.RegisterEnglish(LocalizationKeys.ActivePaused, "Paused");
        service.RegisterEnglish(LocalizationKeys.ActiveNoWallpaper, "No active wallpaper.");
        service.RegisterEnglish(LocalizationKeys.ActiveSchedule, "Schedule");
        service.RegisterEnglish(LocalizationKeys.ActiveScheduleNext, "Next schedule");
        service.RegisterEnglish(LocalizationKeys.ActiveScheduleDay, "Day");
        service.RegisterEnglish(LocalizationKeys.ActiveScheduleNight, "Night");

        // Countdown Formats
        service.RegisterEnglish(LocalizationKeys.FormatCountdownMin, "Next change in {0} min");
        service.RegisterEnglish(LocalizationKeys.FormatCountdownSec, "Next change in {0} sec");
        service.RegisterEnglish(LocalizationKeys.FormatScheduleAt, "{0} at {1}");

        // Settings Interface
        service.RegisterEnglish(LocalizationKeys.SetAppearance, "Appearance");
        service.RegisterEnglish(LocalizationKeys.SetTheme, "Theme");
        service.RegisterEnglish(LocalizationKeys.SetThemeSystem, "System");
        service.RegisterEnglish(LocalizationKeys.SetThemeLight, "Light");
        service.RegisterEnglish(LocalizationKeys.SetThemeDark, "Dark");
        service.RegisterEnglish(LocalizationKeys.SetLanguage, "Language");
        service.RegisterEnglish(LocalizationKeys.SetLanguageDescription, "Choose your preferred interface language.");
        service.RegisterEnglish(LocalizationKeys.SetLanguageRestartNotice, "Restart NiceWP to apply the language change.");

        service.RegisterEnglish(LocalizationKeys.SetEfficiency, "Efficiency Profile");
        service.RegisterEnglish(LocalizationKeys.SetEfficiencyBalanced, "Balanced");
        service.RegisterEnglish(LocalizationKeys.SetEfficiencyEco, "Eco");
        service.RegisterEnglish(LocalizationKeys.SetEfficiencyUnrestricted, "Unrestricted");

        service.RegisterEnglish(LocalizationKeys.SetStartup, "Startup");
        service.RegisterEnglish(LocalizationKeys.SetStartupLaunch, "Launch NiceWP on Windows startup");
        service.RegisterEnglish(LocalizationKeys.SetWallpaperBehavior, "Wallpaper Behavior");
        service.RegisterEnglish(LocalizationKeys.SetRestoreLast, "Restore last wallpaper on startup");

        service.RegisterEnglish(LocalizationKeys.SetWallpaperRotation, "Wallpaper Rotation");
        service.RegisterEnglish(LocalizationKeys.SetRotationMode, "Rotation Mode");
        service.RegisterEnglish(LocalizationKeys.SetRotationOrder, "Rotation Order");
        service.RegisterEnglish(LocalizationKeys.SetRotationSequential, "Sequential");
        service.RegisterEnglish(LocalizationKeys.SetRotationShuffle, "Shuffle");
        service.RegisterEnglish(LocalizationKeys.SetRotationInterval, "Change interval");
        service.RegisterEnglish(LocalizationKeys.SetChangeEvery, "Change every");

        service.RegisterEnglish(LocalizationKeys.SetWallpaperSchedule, "Wallpaper Schedule");
        service.RegisterEnglish(LocalizationKeys.SetScheduleMode, "Schedule Mode");
        service.RegisterEnglish(LocalizationKeys.SetScheduleOff, "Off");
        service.RegisterEnglish(LocalizationKeys.SetScheduleDayNight, "Day & Night");
        service.RegisterEnglish(LocalizationKeys.SetDayWallpaper, "Day wallpaper");
        service.RegisterEnglish(LocalizationKeys.SetNightWallpaper, "Night wallpaper");
        service.RegisterEnglish(LocalizationKeys.SetDayStarts, "Day starts");
        service.RegisterEnglish(LocalizationKeys.SetNightStarts, "Night starts");
        service.RegisterEnglish(LocalizationKeys.SetChooseWallpaper, "Choose wallpaper...");
        service.RegisterEnglish(LocalizationKeys.SetNoWallpaperSelected, "(None selected)");

        service.RegisterEnglish(LocalizationKeys.SetStorageTitle, "Local Storage & Data");
        service.RegisterEnglish(LocalizationKeys.SetOpenStorageFolder, "Open Data Folder");

        // Tray Menu
        service.RegisterEnglish(LocalizationKeys.TrayOpen, "Open NiceWP");
        service.RegisterEnglish(LocalizationKeys.TrayStopWallpaper, "Stop Wallpaper");
        service.RegisterEnglish(LocalizationKeys.TrayExit, "Exit NiceWP");

        // Performance Interface
        service.RegisterEnglish(LocalizationKeys.PerfHealth, "Health");
        service.RegisterEnglish(LocalizationKeys.PerfEfficiency, "Efficiency");
        service.RegisterEnglish(LocalizationKeys.PerfCurrentAction, "Current Action");
        service.RegisterEnglish(LocalizationKeys.PerfCpu, "CPU Usage");
        service.RegisterEnglish(LocalizationKeys.PerfWorkingSet, "Working Set (RAM)");
        service.RegisterEnglish(LocalizationKeys.PerfPrivateMemory, "Private Memory");
        service.RegisterEnglish(LocalizationKeys.PerfThreads, "Threads");
        service.RegisterEnglish(LocalizationKeys.PerfHandles, "Handles");
        service.RegisterEnglish(LocalizationKeys.PerfUptime, "Uptime");
        service.RegisterEnglish(LocalizationKeys.PerfRecentActivity, "Recent Activity");
        service.RegisterEnglish(LocalizationKeys.PerfExport, "Export Report");
        service.RegisterEnglish(LocalizationKeys.PerfHostConnected, "Runtime Host: Connected");
        service.RegisterEnglish(LocalizationKeys.PerfHostDisconnected, "Runtime Host: Disconnected");
        service.RegisterEnglish(LocalizationKeys.PerfRuntimeHostPidUptimeTemplate, "PID: {0} • Uptime: {1}");
        service.RegisterEnglish(LocalizationKeys.PerfLive, "LIVE");
        service.RegisterEnglish(LocalizationKeys.PerfAvgPeakTemplate, "Avg: {0} • Peak: {1}");
        service.RegisterEnglish(LocalizationKeys.PerfPeakTemplate, "Peak: {0}");
        service.RegisterEnglish(LocalizationKeys.PerfSamplesTemplate, "{0} samples (~{0}s)");

        // Status Messages & Feedback
        service.RegisterEnglish(LocalizationKeys.StatusRuntimeRecovered, "Runtime host recovered successfully.");
        service.RegisterEnglish(LocalizationKeys.StatusRestoreFailed, "Failed to restore previous wallpaper.");
        service.RegisterEnglish(LocalizationKeys.StatusWallpaperFileMissing, "Wallpaper file not found on disk.");
        service.RegisterEnglish(LocalizationKeys.StatusSafeStopped, "Wallpaper stopped safely after repeated failures.");
        service.RegisterEnglish(LocalizationKeys.StatusImportCancelled, "Import operation was cancelled.");
        service.RegisterEnglish(LocalizationKeys.StatusUnsupportedFile, "Unsupported file format.");
        service.RegisterEnglish(LocalizationKeys.StatusAlreadyInLibrary, "File is already in your library.");
        service.RegisterEnglish(LocalizationKeys.StatusRotationPaused, "Rotation paused.");
        service.RegisterEnglish(LocalizationKeys.StatusRotationStopped, "Rotation stopped.");
        service.RegisterEnglish(LocalizationKeys.StatusWallpaperApplied, "Applied wallpaper: {0}");
        service.RegisterEnglish(LocalizationKeys.StatusWallpaperStopped, "Wallpaper stopped.");
        service.RegisterEnglish(LocalizationKeys.StatusImportAdded, "Added {0} wallpaper(s).");
        service.RegisterEnglish(LocalizationKeys.StatusImportDuplicates, "{0} item(s) already in library.");
        service.RegisterEnglish(LocalizationKeys.StatusConfirmRemove, "Are you sure you want to remove the selected wallpaper(s) from your library? (Files will not be deleted from disk)");
        service.RegisterEnglish(LocalizationKeys.StatusConfirmRemoveTitle, "Confirm Removal");
        service.RegisterEnglish(LocalizationKeys.StatusCancel, "Cancel");
        service.RegisterEnglish(LocalizationKeys.StatusRemoveButton, "Remove");
    }
}
