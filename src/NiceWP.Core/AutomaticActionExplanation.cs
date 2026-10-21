namespace NiceWP.Core;

/// <summary>
/// Structured record capturing a deterministic automated action performed by the adaptive desktop engine.
/// Provides full explainability and transparency to the user without capturing any sensitive window titles or command lines.
/// </summary>
public sealed record AutomaticActionExplanation
{
    /// <summary>
    /// Type or category of action executed (e.g. "ProfileTransition", "DeepFreeze", "ResourceGuard", "AppRuleApplied").
    /// </summary>
    public string ActionType { get; init; } = string.Empty;

    /// <summary>
    /// Human-readable explanation of why the action was taken.
    /// </summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>
    /// Sanitized process name that triggered the action, if any.
    /// </summary>
    public string? TriggerProcessName { get; init; }

    /// <summary>
    /// Target effective profile associated with this action, if any.
    /// </summary>
    public AdaptiveDesktopProfile? TargetProfile { get; init; }

    /// <summary>
    /// Effective resource budget applied, if any.
    /// </summary>
    public ResourceBudget? TargetBudget { get; init; }

    /// <summary>
    /// Exact UTC timestamp when the action occurred.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
