namespace NiceWP.Core;

/// <summary>
/// Specifies the ordering algorithm for wallpaper rotation.
/// </summary>
public enum RotationOrder
{
    /// <summary>
    /// Sequential traversal in a stable, predictable order.
    /// </summary>
    Sequential = 0,

    /// <summary>
    /// Random shuffle traversal ensuring all eligible items are visited without immediate repetition.
    /// </summary>
    Shuffle = 1,

    /// <summary>
    /// Context-aware selection guided by Smart Scene scoring and wallpaper visual analysis.
    /// </summary>
    Smart = 2
}

/// <summary>
/// Extension methods for <see cref="RotationOrder"/>.
/// </summary>
public static class RotationOrderExtensions
{
    public const string OrderSequential = "Sequential";
    public const string OrderShuffle = "Shuffle";
    public const string OrderSmart = "Smart";

    public static string ToDisplayString(this RotationOrder order, bool isTurkish = false) => order switch
    {
        RotationOrder.Shuffle => isTurkish ? "Karışık" : OrderShuffle,
        RotationOrder.Smart => isTurkish ? "Akıllı Sahne" : OrderSmart,
        _ => isTurkish ? "Sıralı" : OrderSequential
    };

    public static string ToConfigString(this RotationOrder order) => order switch
    {
        RotationOrder.Shuffle => OrderShuffle,
        RotationOrder.Smart => OrderSmart,
        _ => OrderSequential
    };

    public static RotationOrder ParseOrder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return RotationOrder.Sequential;

        var trimmed = value.Trim();
        if (string.Equals(trimmed, OrderShuffle, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Karışık", StringComparison.OrdinalIgnoreCase))
        {
            return RotationOrder.Shuffle;
        }

        if (string.Equals(trimmed, OrderSmart, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Smart Scene", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Akıllı", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Akıllı Sahne", StringComparison.OrdinalIgnoreCase))
        {
            return RotationOrder.Smart;
        }

        return RotationOrder.Sequential;
    }
}
