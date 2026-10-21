namespace NiceWP.Core;

/// <summary>
/// Dictates system resource allocation policy for wallpaper rendering and background activity.
/// </summary>
public enum ResourceBudget
{
    /// <summary>
    /// Automatically balances performance and visual quality based on power state and system pressure.
    /// </summary>
    Automatic = 0,

    /// <summary>
    /// Aggressive power and memory conservation. Enters deep freeze earlier and minimizes rendering overhead.
    /// </summary>
    Low = 1,

    /// <summary>
    /// Default standard budget. Balances fluid visuals with responsible system resource footprint.
    /// </summary>
    Balanced = 2,

    /// <summary>
    /// Prioritizes visual fidelity and seamless continuity while respecting display and session boundaries.
    /// </summary>
    High = 3
}
