namespace NiceWP.Rendering;

/// <summary>
/// Tracks the lifecycle of native window backing within a wallpaper renderer.
/// </summary>
public enum RendererLifecycleState
{
    /// <summary>The window HWND is alive and operational.</summary>
    Alive,

    /// <summary>The OS destroyed the window HWND externally (e.g. Explorer restart).</summary>
    NativeWindowLost,

    /// <summary>The renderer has been disposed and released.</summary>
    Disposed
}
