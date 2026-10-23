namespace NiceWP.Core.Personalization;

using System;

/// <summary>
/// Identifies the discrete nature of a personalization audit event.
/// </summary>
public enum PersonalizationAuditEventType
{
    PreferenceIncreased,
    PreferenceDecreased,
    WallpaperExcluded,
    WallpaperExclusionRemoved,
    PersonalizationReset,
    ManualWallpaperApplied,
    FavoriteToggled
}

/// <summary>
/// Represents an explainable, deterministic record of a personalization decision or state change.
/// </summary>
public sealed class PersonalizationAuditEvent
{
    public PersonalizationAuditEventType EventType { get; init; }
    public string WallpaperId { get; init; } = string.Empty;
    public double? PreviousWeight { get; init; }
    public double? NewWeight { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public string Message { get; init; } = string.Empty;
}
