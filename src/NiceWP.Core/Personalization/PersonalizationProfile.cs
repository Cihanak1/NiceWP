using System.Text.Json.Serialization;

namespace NiceWP.Core.Personalization;

/// <summary>
/// Root data contract representing deterministic local personalization state.
/// Exclusively stored on the local machine with versioning and data minimization.
/// </summary>
public sealed class PersonalizationProfile
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Explicit per-wallpaper preference scores, keyed by stable WallpaperId.
    /// </summary>
    public Dictionary<string, WallpaperPreferenceRecord> WallpaperPreferences { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Bounded category and visual style tendency weights [-1.0 .. +1.0].
    /// </summary>
    public PersonalizationFeatureWeights FeatureWeights { get; set; } = new();

    /// <summary>
    /// Contextual adjustments for specific scenes, keyed by scene name.
    /// </summary>
    public Dictionary<string, double> ScenePreferences { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Aggregated interaction statistics for diagnostic explanations and summary cards.
    /// </summary>
    public PersonalizationInteractionSummary InteractionSummary { get; set; } = new();

    /// <summary>
    /// Set of wallpaper IDs explicitly excluded from automatic smart selections ("Never Show Again").
    /// </summary>
    public HashSet<string> ExcludedWallpaperIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static PersonalizationProfile CreateDefault() => new()
    {
        Version = CurrentVersion,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
        IsEnabled = true
    };
}

/// <summary>
/// Bounded tendency weights across observable visual and resource attributes.
/// All values clamped to range [-1.0, 1.0].
/// </summary>
public sealed class PersonalizationFeatureWeights
{
    public double DarkPreference { get; set; }
    public double LightPreference { get; set; }
    public double StaticPreference { get; set; }
    public double VideoPreference { get; set; }
    public double LowMotionPreference { get; set; }
    public double HighMotionPreference { get; set; }
    public double WarmPreference { get; set; }
    public double NeutralPreference { get; set; }
    public double CoolPreference { get; set; }
    public double LightResourcePreference { get; set; }
    public double ModerateResourcePreference { get; set; }
    public double HeavyResourcePreference { get; set; }

    /// <summary>
    /// Clamps all weights to ensure mathematical stability.
    /// </summary>
    public void ClampAll()
    {
        DarkPreference = Math.Clamp(DarkPreference, -1.0, 1.0);
        LightPreference = Math.Clamp(LightPreference, -1.0, 1.0);
        StaticPreference = Math.Clamp(StaticPreference, -1.0, 1.0);
        VideoPreference = Math.Clamp(VideoPreference, -1.0, 1.0);
        LowMotionPreference = Math.Clamp(LowMotionPreference, -1.0, 1.0);
        HighMotionPreference = Math.Clamp(HighMotionPreference, -1.0, 1.0);
        WarmPreference = Math.Clamp(WarmPreference, -1.0, 1.0);
        NeutralPreference = Math.Clamp(NeutralPreference, -1.0, 1.0);
        CoolPreference = Math.Clamp(CoolPreference, -1.0, 1.0);
        LightResourcePreference = Math.Clamp(LightResourcePreference, -1.0, 1.0);
        ModerateResourcePreference = Math.Clamp(ModerateResourcePreference, -1.0, 1.0);
        HeavyResourcePreference = Math.Clamp(HeavyResourcePreference, -1.0, 1.0);
    }
}

/// <summary>
/// Aggregated user interaction counters.
/// </summary>
public sealed class PersonalizationInteractionSummary
{
    public int TotalExplicitInteractions { get; set; }
    public int MoreLikeThisCount { get; set; }
    public int LessLikeThisCount { get; set; }
    public int ExcludedCount { get; set; }
    public int ManualSelectionCount { get; set; }
    public DateTimeOffset? LastInteractionAtUtc { get; set; }
}
