namespace NiceWP.Core;

/// <summary>
/// Supported visual transition effects for interactive rotation.
/// </summary>
public enum InteractiveTransitionEffect
{
    PageTurn
}

public static class InteractiveTransitionEffectExtensions
{
    public const string EffectPageTurn = "PageTurn";

    public static string ToConfigString(this InteractiveTransitionEffect effect) => EffectPageTurn;

    public static InteractiveTransitionEffect ParseEffect(string? value)
    {
        if (string.Equals(value?.Trim(), EffectPageTurn, StringComparison.OrdinalIgnoreCase))
        {
            return InteractiveTransitionEffect.PageTurn;
        }

        return InteractiveTransitionEffect.PageTurn;
    }
}
