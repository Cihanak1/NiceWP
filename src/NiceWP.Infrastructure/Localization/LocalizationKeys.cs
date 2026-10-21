namespace NiceWP.Infrastructure.Localization;

/// <summary>
/// Strongly-typed string resource key constants for NiceWP application localization.
/// </summary>
public static class LocalizationKeys
{
    // Navigation
    public const string NavLibrary = "Nav.Library";
    public const string NavActiveWallpaper = "Nav.ActiveWallpaper";
    public const string NavPerformance = "Nav.Performance";
    public const string NavSettings = "Nav.Settings";

    // Library Controls
    public const string LibAddWallpaper = "Lib.AddWallpaper";
    public const string LibAddFromUrl = "Lib.AddFromUrl";
    public const string LibAddFolder = "Lib.AddFolder";
    public const string LibSelect = "Lib.Select";
    public const string LibSelectAll = "Lib.SelectAll";
    public const string LibClear = "Lib.Clear";
    public const string LibFavorite = "Lib.Favorite";
    public const string LibUnfavorite = "Lib.Unfavorite";
    public const string LibRemove = "Lib.Remove";
    public const string LibDone = "Lib.Done";
    public const string LibSearchPlaceholder = "Lib.SearchPlaceholder";
    public const string LibFilter = "Lib.Filter";
    public const string LibSort = "Lib.Sort";

    // Library Filters & Sort
    public const string FilterAll = "Filter.All";
    public const string FilterImages = "Filter.Images";
    public const string FilterVideos = "Filter.Videos";
    public const string FilterFavorites = "Filter.Favorites";
    public const string FilterRecent = "Filter.Recent";
    public const string FilterMissing = "Filter.Missing";
    public const string FilterRemote = "Filter.Remote";

    public const string SortRecent = "Sort.Recent";
    public const string SortName = "Sort.Name";
    public const string SortType = "Sort.Type";
    public const string SortFavorites = "Sort.Favorites";

    // Library Context / Item Actions
    public const string ItemActive = "Item.Active";
    public const string ItemApply = "Item.Apply";
    public const string ItemReapply = "Item.Reapply";
    public const string ItemRename = "Item.Rename";
    public const string ItemOpenFileLocation = "Item.OpenFileLocation";
    public const string ItemLocateFile = "Item.LocateFile";
    public const string ItemSelectedCount = "Item.SelectedCount";
    public const string ItemEmptyTitle = "Item.EmptyTitle";
    public const string ItemEmptySubtitle = "Item.EmptySubtitle";

    // Active Wallpaper Controls & Status
    public const string ActivePrevious = "Active.Previous";
    public const string ActivePause = "Active.Pause";
    public const string ActiveResume = "Active.Resume";
    public const string ActiveNext = "Active.Next";
    public const string ActiveStop = "Active.Stop";
    public const string ActiveRotation = "Active.Rotation";
    public const string ActivePosition = "Active.Position";
    public const string ActiveNextChange = "Active.NextChange";
    public const string ActiveOverdue = "Active.Overdue";
    public const string ActiveType = "Active.Type";
    public const string ActiveState = "Active.State";
    public const string ActiveEfficiency = "Active.Efficiency";
    public const string ActiveImage = "Active.Image";
    public const string ActiveVideo = "Active.Video";
    public const string ActiveRunning = "Active.Running";
    public const string ActivePaused = "Active.Paused";
    public const string ActiveNoWallpaper = "Active.NoWallpaper";
    public const string ActiveSchedule = "Active.Schedule";
    public const string ActiveScheduleNext = "Active.ScheduleNext";
    public const string ActiveScheduleDay = "Active.ScheduleDay";
    public const string ActiveScheduleNight = "Active.ScheduleNight";

    // Countdown Formats
    public const string FormatCountdownMin = "Format.CountdownMin";
    public const string FormatCountdownSec = "Format.CountdownSec";
    public const string FormatScheduleAt = "Format.ScheduleAt";

    // Settings Interface
    public const string SetAppearance = "Set.Appearance";
    public const string SetTheme = "Set.Theme";
    public const string SetThemeSystem = "Set.ThemeSystem";
    public const string SetThemeLight = "Set.ThemeLight";
    public const string SetThemeDark = "Set.ThemeDark";
    public const string SetLanguage = "Set.Language";
    public const string SetLanguageDescription = "Set.LanguageDescription";
    public const string SetLanguageRestartNotice = "Set.LanguageRestartNotice";

    public const string SetEfficiency = "Set.Efficiency";
    public const string SetEfficiencyBalanced = "Set.EfficiencyBalanced";
    public const string SetEfficiencyEco = "Set.EfficiencyEco";
    public const string SetEfficiencyUnrestricted = "Set.EfficiencyUnrestricted";

    public const string SetStartup = "Set.Startup";
    public const string SetStartupLaunch = "Set.StartupLaunch";
    public const string SetWallpaperBehavior = "Set.WallpaperBehavior";
    public const string SetRestoreLast = "Set.RestoreLast";

    public const string SetWallpaperRotation = "Set.WallpaperRotation";
    public const string SetRotationMode = "Set.RotationMode";
    public const string SetRotationOrder = "Set.RotationOrder";
    public const string SetRotationSequential = "Set.RotationSequential";
    public const string SetRotationShuffle = "Set.RotationShuffle";
    public const string SetRotationInterval = "Set.RotationInterval";
    public const string SetChangeEvery = "Set.ChangeEvery";

    public const string SetWallpaperSchedule = "Set.WallpaperSchedule";
    public const string SetScheduleMode = "Set.ScheduleMode";
    public const string SetScheduleOff = "Set.ScheduleOff";
    public const string SetScheduleDayNight = "Set.ScheduleDayNight";
    public const string SetDayWallpaper = "Set.DayWallpaper";
    public const string SetNightWallpaper = "Set.NightWallpaper";
    public const string SetDayStarts = "Set.DayStarts";
    public const string SetNightStarts = "Set.NightStarts";
    public const string SetChooseWallpaper = "Set.ChooseWallpaper";
    public const string SetNoWallpaperSelected = "Set.NoWallpaperSelected";

    public const string SetStorageTitle = "Set.StorageTitle";
    public const string SetOpenStorageFolder = "Set.OpenStorageFolder";

    // Performance Interface
    public const string PerfHealth = "Perf.Health";
    public const string PerfEfficiency = "Perf.Efficiency";
    public const string PerfCurrentAction = "Perf.CurrentAction";
    public const string PerfCpu = "Perf.Cpu";
    public const string PerfWorkingSet = "Perf.WorkingSet";
    public const string PerfPrivateMemory = "Perf.PrivateMemory";
    public const string PerfThreads = "Perf.Threads";
    public const string PerfHandles = "Perf.Handles";
    public const string PerfUptime = "Perf.Uptime";
    public const string PerfRecentActivity = "Perf.RecentActivity";
    public const string PerfExport = "Perf.Export";
    public const string PerfHostConnected = "Perf.HostConnected";
    public const string PerfHostDisconnected = "Perf.HostDisconnected";

    // Status Messages & Feedback
    public const string StatusRuntimeRecovered = "Status.RuntimeRecovered";
    public const string StatusRestoreFailed = "Status.RestoreFailed";
    public const string StatusWallpaperFileMissing = "Status.WallpaperFileMissing";
    public const string StatusSafeStopped = "Status.SafeStopped";
    public const string StatusImportCancelled = "Status.ImportCancelled";
    public const string StatusUnsupportedFile = "Status.UnsupportedFile";
    public const string StatusAlreadyInLibrary = "Status.AlreadyInLibrary";
    public const string StatusRotationPaused = "Status.RotationPaused";
    public const string StatusRotationStopped = "Status.RotationStopped";
    public const string StatusWallpaperApplied = "Status.WallpaperApplied";
    public const string StatusWallpaperStopped = "Status.WallpaperStopped";
    public const string StatusImportAdded = "Status.ImportAdded";
    public const string StatusImportDuplicates = "Status.ImportDuplicates";
    public const string StatusConfirmRemove = "Status.ConfirmRemove";
    public const string StatusConfirmRemoveTitle = "Status.ConfirmRemoveTitle";
    public const string StatusCancel = "Status.Cancel";
    public const string StatusRemoveButton = "Status.RemoveButton";
}
