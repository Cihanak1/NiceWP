using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;

namespace NiceWP.Infrastructure.Adaptive;

/// <summary>
/// Encapsulates the external contextual parameters and user configurations
/// evaluated by <see cref="AdaptiveDesktopStateResolver"/> to produce an <see cref="EffectiveDesktopState"/>.
/// </summary>
public sealed record AdaptiveStateResolverInput
{
    public AdaptiveDesktopProfile ConfiguredProfile { get; init; } = AdaptiveDesktopProfile.Automatic;
    public ResourceBudget ConfiguredBudget { get; init; } = ResourceBudget.Balanced;
    public string? ForegroundProcessName { get; init; }
    public bool IsFullscreenActive { get; init; }
    public bool IsOnBattery { get; init; }
    public bool IsBatterySaverActive { get; init; }
    public bool IsSessionLocked { get; init; }
    public bool IsDisplayOff { get; init; }
    public bool IsManualPauseActive { get; init; }
    public bool HasActiveWallpaper { get; init; }
    public string CurrentWallpaperType { get; init; } = IpcConstants.WallpaperTypeNone;
    public bool IsResourcePressureSustained { get; init; }
    public string? PresentationWallpaperId { get; init; }
    public AdaptiveDesktopProfile? MatchedAppRuleProfile { get; init; }
    public string? MatchedAppRuleWallpaperId { get; init; }
    public bool MatchedAppRuleDeepFreeze { get; init; }
    public bool MatchedAppRulePause { get; init; }
}
