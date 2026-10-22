namespace NiceWP.Core;

/// <summary>
/// Lifecycle states of the interactive wallpaper corner page-turn gesture.
/// </summary>
public enum TransitionState
{
    /// <summary>No transition or hover preview active.</summary>
    Idle,

    /// <summary>Pointer is hovered over the top-right hot corner; subtle corner lift shown.</summary>
    HoverPreview,

    /// <summary>Pointer is captured and user is actively dragging the corner.</summary>
    Dragging,

    /// <summary>User released above the commit threshold; animating curl offscreen to complete.</summary>
    Completing,

    /// <summary>User released below threshold or pressed ESC; animating curl back to rest.</summary>
    Cancelling
}
