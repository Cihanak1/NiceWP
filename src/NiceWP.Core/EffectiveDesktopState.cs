namespace NiceWP.Core;

/// <summary>
/// Immutable snapshot representing the resolved effective operational state of the desktop engine.
/// Produced deterministically by the adaptive state resolver for any given context.
/// </summary>
public sealed record EffectiveDesktopState
{
    /// <summary>
    /// The concrete effective profile currently applied (never Automatic in the output).
    /// </summary>
    public AdaptiveDesktopProfile EffectiveProfile { get; init; } = AdaptiveDesktopProfile.Normal;

    /// <summary>
    /// Whether wallpaper playback should be paused under this state.
    /// </summary>
    public bool ShouldPause { get; init; }

    /// <summary>
    /// Whether video resources should be completely released and decoders unbound (Deep Freeze).
    /// </summary>
    public bool ShouldDeepFreeze { get; init; }

    /// <summary>
    /// Whether automated wallpaper rotation is permitted to cycle.
    /// </summary>
    public bool AllowRotation { get; init; } = true;

    /// <summary>
    /// Whether automated scheduled time-of-day transitions are permitted to apply immediately.
    /// </summary>
    public bool AllowScheduleApply { get; init; } = true;

    /// <summary>
    /// Optional wallpaper ID designated by rule or profile (e.g. Presentation profile).
    /// </summary>
    public string? PreferredWallpaperId { get; init; }

    /// <summary>
    /// The effective resource budget guiding rendering fidelity and grace periods.
    /// </summary>
    public ResourceBudget ResourceBudget { get; init; } = ResourceBudget.Balanced;

    /// <summary>
    /// Human-readable explanation of why this state was resolved.
    /// </summary>
    public string Reason { get; init; } = "Operating normally";

    /// <summary>
    /// Active foreground process that triggered or informed this state, if applicable (sanitized, privacy-first).
    /// </summary>
    public string? ActiveProcessName { get; init; }

    /// <summary>
    /// Convenience default normal state.
    /// </summary>
    public static readonly EffectiveDesktopState Default = new();
}
