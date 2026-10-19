namespace NiceWP.Core;

/// <summary>
/// Specifies the ordering algorithm for wallpaper rotation.
/// </summary>
public enum RotationOrder
{
    /// <summary>
    /// Sequential traversal in a stable, predictable order.
    /// </summary>
    Sequential,

    /// <summary>
    /// Random shuffle traversal ensuring all eligible items are visited without immediate repetition.
    /// </summary>
    Shuffle
}

/// <summary>
/// Extension methods for <see cref="RotationOrder"/>.
/// </summary>
public static class RotationOrderExtensions
{
    public const string OrderSequential = "Sequential";
    public const string OrderShuffle = "Shuffle";

    public static string ToDisplayString(this RotationOrder order) => order switch
    {
        RotationOrder.Shuffle => OrderShuffle,
        _ => OrderSequential
    };

    public static string ToConfigString(this RotationOrder order) => order switch
    {
        RotationOrder.Shuffle => OrderShuffle,
        _ => OrderSequential
    };

    public static RotationOrder ParseOrder(string? value)
    {
        if (string.Equals(value?.Trim(), OrderShuffle, StringComparison.OrdinalIgnoreCase))
        {
            return RotationOrder.Shuffle;
        }

        return RotationOrder.Sequential;
    }
}
