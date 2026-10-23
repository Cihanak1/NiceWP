namespace NiceWP.Core.Personalization;

/// <summary>
/// Breakdown of personalization scoring metrics evaluated for a candidate wallpaper.
/// Clamped within [-20.0, +20.0] so it cannot overwhelm environmental/scene hard constraints.
/// </summary>
public sealed record PersonalizationScoreResult
{
    public double Total { get; init; }

    public double WallpaperPreference { get; init; }

    public double MediaPreference { get; init; }

    public double BrightnessPreference { get; init; }

    public double MotionPreference { get; init; }

    public double ColorPreference { get; init; }

    public double ResourcePreference { get; init; }

    public IReadOnlyList<string> ReasonsEn { get; init; } = [];

    public IReadOnlyList<string> ReasonsTr { get; init; } = [];

    public static PersonalizationScoreResult Empty { get; } = new()
    {
        Total = 0.0,
        ReasonsEn = [],
        ReasonsTr = []
    };
}
