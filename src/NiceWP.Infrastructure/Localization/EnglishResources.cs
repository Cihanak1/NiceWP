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

        // Adaptive Desktop Intelligence
        service.RegisterEnglish(LocalizationKeys.SetAdaptiveHeader, "Adaptive Desktop Intelligence");
        service.RegisterEnglish(LocalizationKeys.SetAdaptiveSubtitle, "Configure automatic behavior profiles and hardware resource limits.");
        service.RegisterEnglish(LocalizationKeys.SetAdaptiveDefaultProfile, "Default Profile");
        service.RegisterEnglish(LocalizationKeys.SetAdaptiveResourceBudget, "Resource Budget");
        service.RegisterEnglish(LocalizationKeys.SetAppRulesHeader, "Application Rules");
        service.RegisterEnglish(LocalizationKeys.SetAppRulesSubtitle, "Assign specific desktop profiles or behaviors when selected applications are active.");
        service.RegisterEnglish(LocalizationKeys.SetAddAppRule, "Add Rule");
        service.RegisterEnglish(LocalizationKeys.SetAddFromRunning, "Add from Running…");
        service.RegisterEnglish(LocalizationKeys.SetPresentationWallpaper, "Presentation Profile Wallpaper (Optional)");
        service.RegisterEnglish(LocalizationKeys.TrayProfile, "Desktop Profile");
        service.RegisterEnglish(LocalizationKeys.TrayBudget, "Resource Budget");

        // Interactive Rotation / Page Turn (0.0.18)
        service.RegisterEnglish(LocalizationKeys.SetInteractiveTransitionHeader, "Interactive Page Turn");
        service.RegisterEnglish(LocalizationKeys.SetInteractiveTransitionSubtitle, "Drag from the top-right corner to peel and preview the next wallpaper.");
        service.RegisterEnglish(LocalizationKeys.SetInteractiveTransitionGesture, "Corner Turn Gesture");
        service.RegisterEnglish(LocalizationKeys.SetPaperWave, "Paper Wave");
        service.RegisterEnglish(LocalizationKeys.SetTransitionEffect, "Transition Effect");
        service.RegisterEnglish(LocalizationKeys.SetTransitionSensitivity, "Drag Sensitivity");
        service.RegisterEnglish(LocalizationKeys.SetTransitionQuality, "Visual Quality");
        service.RegisterEnglish(LocalizationKeys.TransitionEffectPageTurn, "Page Curl");
        service.RegisterEnglish(LocalizationKeys.TransitionSensitivityLow, "Low (Longer Drag)");
        service.RegisterEnglish(LocalizationKeys.TransitionSensitivityNormal, "Normal");
        service.RegisterEnglish(LocalizationKeys.TransitionSensitivityHigh, "High (Shorter Drag)");
        service.RegisterEnglish(LocalizationKeys.TransitionQualityAuto, "Auto (Based on Budget)");
        service.RegisterEnglish(LocalizationKeys.TransitionQualityLow, "Low (No Wave, Light Shadow)");
        service.RegisterEnglish(LocalizationKeys.TransitionQualityBalanced, "Balanced (Standard)");
        service.RegisterEnglish(LocalizationKeys.TransitionQualityHigh, "High (Full Wave & Shadow)");
        service.RegisterEnglish(LocalizationKeys.TransitionStatusHover, "Corner ready: drag down-left to turn page.");
        service.RegisterEnglish(LocalizationKeys.TransitionStatusDragging, "Turning page: {0:P0}");
        service.RegisterEnglish(LocalizationKeys.TransitionStatusCancelled, "Transition cancelled.");
        service.RegisterEnglish(LocalizationKeys.TransitionStatusCommitted, "Advanced to next wallpaper: {0}");

        // Smart Scenes (0.0.19)
        service.RegisterEnglish(LocalizationKeys.SetSmartScenesHeader, "Smart Scenes");
        service.RegisterEnglish(LocalizationKeys.SetSmartScenesSubtitle, "NiceWP automatically selects a wallpaper that fits your current desktop context.");
        service.RegisterEnglish(LocalizationKeys.SetSmartScenesEnabled, "Smart Scenes");
        service.RegisterEnglish(LocalizationKeys.SetSmartScenesScene, "Scene");
        service.RegisterEnglish(LocalizationKeys.SetSmartRotation, "Smart Rotation");
        service.RegisterEnglish(LocalizationKeys.SetSmartScenesAvoidRecent, "Avoid Recent Wallpapers");
        service.RegisterEnglish(LocalizationKeys.SetSmartScenesPreferFavorites, "Prefer Favorites");

        service.RegisterEnglish(LocalizationKeys.SmartSceneOff, "Off");
        service.RegisterEnglish(LocalizationKeys.SmartSceneAutomatic, "Automatic");
        service.RegisterEnglish(LocalizationKeys.SmartSceneFocus, "Focus");
        service.RegisterEnglish(LocalizationKeys.SmartSceneGaming, "Gaming");
        service.RegisterEnglish(LocalizationKeys.SmartSceneRelax, "Relax");
        service.RegisterEnglish(LocalizationKeys.SmartSceneNight, "Night");
        service.RegisterEnglish(LocalizationKeys.SmartSceneBatterySaver, "Battery Saver");

        service.RegisterEnglish(LocalizationKeys.FilterDark, "Dark");
        service.RegisterEnglish(LocalizationKeys.FilterLight, "Light");
        service.RegisterEnglish(LocalizationKeys.FilterLowResource, "Low Resource");
        service.RegisterEnglish(LocalizationKeys.FilterHighResolution, "High Resolution");
        service.RegisterEnglish(LocalizationKeys.FilterStatic, "Static");
        service.RegisterEnglish(LocalizationKeys.FilterVideo, "Video");
        service.RegisterEnglish(LocalizationKeys.FilterLowMotion, "Low Motion");

        service.RegisterEnglish(LocalizationKeys.ActiveSmartSceneLabel, "Smart Scene");
        service.RegisterEnglish(LocalizationKeys.ActiveSmartSceneSelectedFor, "Selected for {0}");
        service.RegisterEnglish(LocalizationKeys.ActiveSmartSceneWhy, "Why");
        service.RegisterEnglish(LocalizationKeys.ActiveSelectedForFocus, "Selected for Focus");
        service.RegisterEnglish(LocalizationKeys.ActiveReasonDark, "Dark");
        service.RegisterEnglish(LocalizationKeys.ActiveReasonLowMotion, "Low motion");
        service.RegisterEnglish(LocalizationKeys.ActiveReasonLightResource, "Light resource usage");

        service.RegisterEnglish(LocalizationKeys.StatusAnalyzingWallpapers, "Analyzing {0} wallpapers...");

        // Discover / Online Library (0.0.20)
        service.RegisterEnglish(LocalizationKeys.NavDiscover, "Discover");
        service.RegisterEnglish(LocalizationKeys.LibraryTabMyLibrary, "My Library");
        service.RegisterEnglish(LocalizationKeys.LibraryTabDiscover, "Discover");
        service.RegisterEnglish(LocalizationKeys.DiscoverSearchPlaceholder, "Search wallpapers...");
        service.RegisterEnglish(LocalizationKeys.DiscoverProvider, "Provider");
        service.RegisterEnglish(LocalizationKeys.DiscoverProviderAll, "All");
        service.RegisterEnglish(LocalizationKeys.DiscoverOrientation, "Orientation");
        service.RegisterEnglish(LocalizationKeys.DiscoverOrientationAll, "All");
        service.RegisterEnglish(LocalizationKeys.DiscoverOrientationLandscape, "Landscape");
        service.RegisterEnglish(LocalizationKeys.DiscoverResolution, "Resolution");
        service.RegisterEnglish(LocalizationKeys.DiscoverResolutionAll, "All");
        service.RegisterEnglish(LocalizationKeys.DiscoverMedia, "Media");
        service.RegisterEnglish(LocalizationKeys.DiscoverMediaAll, "All");
        service.RegisterEnglish(LocalizationKeys.DiscoverMediaImages, "Images");
        service.RegisterEnglish(LocalizationKeys.DiscoverMediaVideos, "Videos");
        service.RegisterEnglish(LocalizationKeys.DiscoverLicense, "License");
        service.RegisterEnglish(LocalizationKeys.DiscoverLicenseRecommended, "Recommended");
        service.RegisterEnglish(LocalizationKeys.DiscoverLicensePublicDomain, "Public Domain");
        service.RegisterEnglish(LocalizationKeys.DiscoverLicenseCC0, "CC0 (Public Domain)");
        service.RegisterEnglish(LocalizationKeys.DiscoverLicenseCCBY, "CC BY (Attribution)");
        service.RegisterEnglish(LocalizationKeys.DiscoverLicenseCCBYSA, "CC BY-SA (Attribution-ShareAlike)");
        service.RegisterEnglish(LocalizationKeys.DiscoverSort, "Sort");
        service.RegisterEnglish(LocalizationKeys.DiscoverSortRelevant, "Relevant");
        service.RegisterEnglish(LocalizationKeys.DiscoverSortNewest, "Newest");
        service.RegisterEnglish(LocalizationKeys.DiscoverSortPopular, "Popular");
        service.RegisterEnglish(LocalizationKeys.DiscoverPresetNature, "Nature");
        service.RegisterEnglish(LocalizationKeys.DiscoverPresetSpace, "Space");
        service.RegisterEnglish(LocalizationKeys.DiscoverPresetCity, "City");
        service.RegisterEnglish(LocalizationKeys.DiscoverPresetMinimal, "Minimal");
        service.RegisterEnglish(LocalizationKeys.DiscoverPresetAbstract, "Abstract");
        service.RegisterEnglish(LocalizationKeys.DiscoverPresetDark, "Dark");
        service.RegisterEnglish(LocalizationKeys.DiscoverPresetLandscape, "Landscape");
        service.RegisterEnglish(LocalizationKeys.DiscoverSource, "Source");
        service.RegisterEnglish(LocalizationKeys.DiscoverLicenseLabel, "License");
        service.RegisterEnglish(LocalizationKeys.DiscoverCreator, "Creator");
        service.RegisterEnglish(LocalizationKeys.DiscoverAddToLibrary, "Add to Library");
        service.RegisterEnglish(LocalizationKeys.DiscoverAlreadyInLibrary, "Already in Library");
        service.RegisterEnglish(LocalizationKeys.DiscoverPreview, "Preview");
        service.RegisterEnglish(LocalizationKeys.DiscoverOpenOriginal, "Open Original");
        service.RegisterEnglish(LocalizationKeys.DiscoverDownloading, "Downloading");
        service.RegisterEnglish(LocalizationKeys.DiscoverProcessing, "Processing");
        service.RegisterEnglish(LocalizationKeys.DiscoverAdded, "Added");
        service.RegisterEnglish(LocalizationKeys.DiscoverDownloadFailed, "Download Failed");
        service.RegisterEnglish(LocalizationKeys.DiscoverLicenseDisclaimer, "License information is provided by the source.");
        service.RegisterEnglish(LocalizationKeys.DiscoverNoInternet, "No internet connection.");
        service.RegisterEnglish(LocalizationKeys.DiscoverRateLimited, "Request rate limit reached. Please try again later.");
        service.RegisterEnglish(LocalizationKeys.DiscoverNoResults, "No wallpapers found matching your search.");
        service.RegisterEnglish(LocalizationKeys.SetOnlineLibraryHeader, "Online Library");
        service.RegisterEnglish(LocalizationKeys.SetOnlineLibrarySubtitle, "Configure open-license wallpaper discovery providers.");
        service.RegisterEnglish(LocalizationKeys.SetOnlineOpenverse, "Openverse");
        service.RegisterEnglish(LocalizationKeys.SetOnlineWikimedia, "Wikimedia Commons");
        service.RegisterEnglish(LocalizationKeys.SetOnlineNasa, "NASA");
    }
}
