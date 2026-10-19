using NiceWP.Core;

namespace NiceWP.Infrastructure.Rotation;

/// <summary>
/// Snapshot of the current wallpaper rotation state for UI presentation and diagnostics.
/// </summary>
public sealed record WallpaperRotationStatus(
    RotationMode Mode,
    RotationOrder Order,
    TimeSpan Interval,
    DateTimeOffset? NextDueTimeUtc,
    bool IsOverdue,
    int TotalEligibleCount,
    int CurrentIndex,
    string? CurrentItemId,
    string? CurrentWallpaperName,
    bool IsPaused,
    string? PauseReasons)
{
    public bool IsActive => Mode != RotationMode.Off;

    public string FormattedPosition =>
        TotalEligibleCount > 0 && CurrentIndex >= 0
            ? $"{CurrentIndex + 1} / {TotalEligibleCount}"
            : TotalEligibleCount > 0
                ? $"- / {TotalEligibleCount}"
                : "0 / 0";

    public string FormattedState =>
        Mode switch
        {
            RotationMode.All => $"All Library • {Order.ToDisplayString()}",
            RotationMode.Favorites => $"Favorites • {Order.ToDisplayString()}",
            _ => "Off"
        };
}
