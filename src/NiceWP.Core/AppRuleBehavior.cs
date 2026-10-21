namespace NiceWP.Core;

/// <summary>
/// Specifies the action or profile to trigger when a user-configured application is detected in the foreground.
/// </summary>
public enum AppRuleBehavior
{
    NoAction = 0,
    Focus = 1,
    Gaming = 2,
    Presentation = 3,
    Cinema = 4,
    Pause = 5,
    DeepFreeze = 6,
    UseWallpaper = 7
}
