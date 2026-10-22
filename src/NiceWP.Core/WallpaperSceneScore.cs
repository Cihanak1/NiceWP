namespace NiceWP.Core;

/// <summary>
/// Breakdown of deterministic scoring metrics evaluated for a candidate wallpaper within a Smart Scene context.
/// Final score is clamped within [0, 100].
/// </summary>
public sealed record WallpaperSceneScore
{
    public double TotalScore { get; init; }

    public double SceneMatchScore { get; init; }

    public double ResourceScore { get; init; }

    public double BrightnessScore { get; init; }

    public double MotionScore { get; init; }

    public double DiversityScore { get; init; }

    public double FavoriteBonus { get; init; }

    public double RecentPenalty { get; init; }

    public string ReasonSummary { get; init; } = string.Empty;

    public string ReasonSummaryTr { get; init; } = string.Empty;

    public string GetLocalizedReason(bool isTurkish) =>
        isTurkish ? ReasonSummaryTr : ReasonSummary;
}
