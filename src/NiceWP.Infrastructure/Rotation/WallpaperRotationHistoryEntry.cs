namespace NiceWP.Infrastructure.Rotation;

/// <summary>
/// Represents an in-memory record of a wallpaper rotation event for rotation history and diagnostics.
/// </summary>
public sealed record WallpaperRotationHistoryEntry(
    string ItemId,
    string DisplayName,
    DateTimeOffset AppliedAtUtc,
    string Trigger, // "Manual", "Auto", "Next", "Previous", "Startup"
    bool Success,
    string? FailureReason = null);
