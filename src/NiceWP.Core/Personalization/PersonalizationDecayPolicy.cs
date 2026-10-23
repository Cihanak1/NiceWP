namespace NiceWP.Core.Personalization;

/// <summary>
/// Deterministic timestamp-based decay policy for personalization signals.
/// Ensures stale preferences gently relax toward neutral over months without aggressive resets.
/// Evaluated lazily to prevent CPU/IO spikes on startup.
/// </summary>
public static class PersonalizationDecayPolicy
{
    public const double GracePeriodDays = 30.0;
    public const double ModerateDecayThresholdDays = 90.0;
    public const double LongTermDecayThresholdDays = 180.0;
    public const double MinimumDecayFloor = 0.20;

    /// <summary>
    /// Computes a multiplier [0.20 .. 1.0] representing the freshness of a preference signal.
    /// - Less than 30 days: 100% full strength (1.0).
    /// - 30 to 90 days: Gentle linear decay from 1.0 down to 0.85 (15% reduction).
    /// - 90 to 180 days: Linear decay from 0.85 down to 0.60.
    /// - Beyond 180 days: Smooth asymptotic relaxation toward floor of 0.20.
    /// </summary>
    public static double CalculateDecayFactor(DateTimeOffset lastInteractedAtUtc, DateTimeOffset nowUtc)
    {
        var elapsedDays = Math.Max(0.0, (nowUtc - lastInteractedAtUtc).TotalDays);

        if (elapsedDays < GracePeriodDays)
        {
            return 1.0;
        }

        if (elapsedDays <= ModerateDecayThresholdDays)
        {
            double progress = (elapsedDays - GracePeriodDays) / (ModerateDecayThresholdDays - GracePeriodDays);
            return 1.0 - (progress * 0.15);
        }

        if (elapsedDays <= LongTermDecayThresholdDays)
        {
            double progress = (elapsedDays - ModerateDecayThresholdDays) / (LongTermDecayThresholdDays - ModerateDecayThresholdDays);
            return 0.85 - (progress * 0.25);
        }

        double extraMonths = (elapsedDays - LongTermDecayThresholdDays) / 30.0;
        double decayed = 0.60 * Math.Pow(0.95, extraMonths);
        return Math.Max(MinimumDecayFloor, Math.Round(decayed, 3));
    }

    /// <summary>
    /// Applies decay to a given raw weight.
    /// </summary>
    public static double GetDecayedWeight(double rawWeight, DateTimeOffset lastInteractedAtUtc, DateTimeOffset nowUtc)
    {
        double factor = CalculateDecayFactor(lastInteractedAtUtc, nowUtc);
        return Math.Clamp(rawWeight * factor, -1.0, 1.0);
    }
}
