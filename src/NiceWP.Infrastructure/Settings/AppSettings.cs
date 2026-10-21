using System.Text.Json.Serialization;
using NiceWP.Core;
using NiceWP.Infrastructure.Adaptive;
using NiceWP.Infrastructure.Ipc;

namespace NiceWP.Infrastructure.Settings;

/// <summary>
/// Represents persisted user and runtime settings stored in %LocalAppData%\NiceWP\settings.json.
/// Autostart state is intentionally excluded so the Windows Registry remains the single source of truth.
/// </summary>
public sealed class AppSettings
{
    public const string ThemeSystem = "System";
    public const string ThemeLight = "Light";
    public const string ThemeDark = "Dark";

    public bool RestoreLastWallpaper { get; set; } = true;

    public string LastWallpaperType { get; set; } = IpcConstants.WallpaperTypeNone;

    public string? LastWallpaperPath { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastWallpaperState { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Language { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Theme { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EfficiencyProfile { get; set; }

    public string RotationMode { get; set; } = RotationModeExtensions.ModeOff;

    public string RotationOrder { get; set; } = RotationOrderExtensions.OrderSequential;

    public int RotationIntervalMinutes { get; set; } = 30;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastRotationItemId { get; set; }

    public const string DefaultDayStartTime = "08:00";
    public const string DefaultNightStartTime = "20:00";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScheduleMode { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DayStartTime { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NightStartTime { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DayWallpaperId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NightWallpaperId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PresentationWallpaperId { get; set; }

    public List<AppRule> AppRules { get; set; } = new();

    [JsonIgnore]
    public string EffectiveScheduleMode => ScheduleMode ?? ScheduleModeExtensions.ModeOff;

    [JsonIgnore]
    public string EffectiveDayStartTime => DayStartTime ?? DefaultDayStartTime;

    [JsonIgnore]
    public string EffectiveNightStartTime => NightStartTime ?? DefaultNightStartTime;

    public static AppSettings CreateDefault() => new()
    {
        RestoreLastWallpaper = true,
        LastWallpaperType = IpcConstants.WallpaperTypeNone,
        LastWallpaperPath = null,
        LastWallpaperState = null,
        Language = null,
        Theme = null,
        EfficiencyProfile = null,
        RotationMode = RotationModeExtensions.ModeOff,
        RotationOrder = RotationOrderExtensions.OrderSequential,
        RotationIntervalMinutes = 30,
        LastRotationItemId = null,
        ScheduleMode = null,
        DayStartTime = null,
        NightStartTime = null,
        DayWallpaperId = null,
        NightWallpaperId = null,
        PresentationWallpaperId = null,
        AppRules = new List<AppRule>()
    };

    /// <summary>
    /// Returns a normalized copy where invalid wallpaper types or inconsistent paths
    /// fall back to safe default values.
    /// </summary>
    public AppSettings Normalize()
    {
        var normalizedType = NormalizeWallpaperType(LastWallpaperType);
        var normalizedPath = string.IsNullOrWhiteSpace(LastWallpaperPath) ? null : LastWallpaperPath.Trim();

        if (normalizedType == IpcConstants.WallpaperTypeNone || normalizedPath == null)
        {
            normalizedType = IpcConstants.WallpaperTypeNone;
            normalizedPath = null;
        }

        return new AppSettings
        {
            RestoreLastWallpaper = RestoreLastWallpaper,
            LastWallpaperType = normalizedType,
            LastWallpaperPath = normalizedPath,
            LastWallpaperState = string.IsNullOrWhiteSpace(LastWallpaperState) ? null : LastWallpaperState.Trim(),
            Language = NormalizeLanguage(Language),
            Theme = NormalizeTheme(Theme),
            EfficiencyProfile = NormalizeEfficiencyProfile(EfficiencyProfile),
            RotationMode = NormalizeRotationMode(RotationMode),
            RotationOrder = NormalizeRotationOrder(RotationOrder),
            RotationIntervalMinutes = NormalizeRotationIntervalMinutes(RotationIntervalMinutes),
            LastRotationItemId = string.IsNullOrWhiteSpace(LastRotationItemId) ? null : LastRotationItemId.Trim(),
            ScheduleMode = NormalizeScheduleMode(ScheduleMode),
            DayStartTime = NormalizeDayStartTime(DayStartTime),
            NightStartTime = NormalizeNightStartTime(NightStartTime),
            DayWallpaperId = string.IsNullOrWhiteSpace(DayWallpaperId) ? null : DayWallpaperId.Trim(),
            NightWallpaperId = string.IsNullOrWhiteSpace(NightWallpaperId) ? null : NightWallpaperId.Trim(),
            PresentationWallpaperId = string.IsNullOrWhiteSpace(PresentationWallpaperId) ? null : PresentationWallpaperId.Trim(),
            AppRules = AppRuleNormalizer.NormalizeRules(AppRules).ToList()
        };
    }

    public static bool IsValidWallpaperType(string? wallpaperType) =>
        string.Equals(wallpaperType, IpcConstants.WallpaperTypeNone, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(wallpaperType, IpcConstants.WallpaperTypeImage, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(wallpaperType, IpcConstants.WallpaperTypeVideo, StringComparison.OrdinalIgnoreCase);

    public static string NormalizeWallpaperType(string? wallpaperType)
    {
        if (string.Equals(wallpaperType, IpcConstants.WallpaperTypeImage, StringComparison.OrdinalIgnoreCase))
        {
            return IpcConstants.WallpaperTypeImage;
        }

        if (string.Equals(wallpaperType, IpcConstants.WallpaperTypeVideo, StringComparison.OrdinalIgnoreCase))
        {
            return IpcConstants.WallpaperTypeVideo;
        }

        return IpcConstants.WallpaperTypeNone;
    }

    public static string? NormalizeLanguage(string? language)
    {
        var parsed = AppLanguageExtensions.ParseLanguage(language);
        return parsed == AppLanguage.System ? null : parsed.ToConfigString();
    }

    public static string? NormalizeTheme(string? theme)
    {
        if (string.Equals(theme?.Trim(), ThemeLight, StringComparison.OrdinalIgnoreCase))
        {
            return ThemeLight;
        }

        if (string.Equals(theme?.Trim(), ThemeDark, StringComparison.OrdinalIgnoreCase))
        {
            return ThemeDark;
        }

        return null;
    }

    public static string? NormalizeEfficiencyProfile(string? profile)
    {
        var parsed = EfficiencyPolicyMatrix.ParseProfile(profile);
        return parsed == NiceWP.Core.EfficiencyProfile.Balanced ? null : parsed.ToDisplayString();
    }

    public static string NormalizeRotationMode(string? mode) =>
        RotationModeExtensions.ParseMode(mode).ToConfigString();

    public static string NormalizeRotationOrder(string? order) =>
        RotationOrderExtensions.ParseOrder(order).ToConfigString();

    public static int NormalizeRotationIntervalMinutes(int minutes) => minutes switch
    {
        5 or 15 or 30 or 60 or 180 or 360 or 720 or 1440 => minutes,
        _ => 30
    };

    public static string? NormalizeScheduleMode(string? mode)
    {
        var parsed = ScheduleModeExtensions.ParseMode(mode);
        return parsed == NiceWP.Core.ScheduleMode.Off ? null : parsed.ToConfigString();
    }

    public static string? NormalizeDayStartTime(string? time)
    {
        if (string.IsNullOrWhiteSpace(time)) return null;
        if (!TimeOnly.TryParse(time.Trim(), out var parsed)) return null;
        var formatted = parsed.ToString("HH:mm");
        return formatted == DefaultDayStartTime ? null : formatted;
    }

    public static string? NormalizeNightStartTime(string? time)
    {
        if (string.IsNullOrWhiteSpace(time)) return null;
        if (!TimeOnly.TryParse(time.Trim(), out var parsed)) return null;
        var formatted = parsed.ToString("HH:mm");
        return formatted == DefaultNightStartTime ? null : formatted;
    }
}
