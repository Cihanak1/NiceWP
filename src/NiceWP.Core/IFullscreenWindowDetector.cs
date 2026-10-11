namespace NiceWP.Core;

/// <summary>
/// Detects whether an external foreground application window is currently covering its monitor in fullscreen mode.
/// </summary>
public interface IFullscreenWindowDetector
{
    bool IsFullscreenWindowActive();
}
