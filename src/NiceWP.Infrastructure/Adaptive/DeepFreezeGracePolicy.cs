namespace NiceWP.Infrastructure.Adaptive;

/// <summary>
/// Governs the transition delay between normal video pause and complete Deep Freeze.
/// While normal pause immediately stops playback rendering, Deep Freeze detaches video sources
/// and releases hardware decoders. A grace period (default 30 seconds) prevents rapid decoder thrashing
/// during brief gaming alt-tabs, while critical power states trigger immediate deep freeze with zero delay.
/// </summary>
public sealed class DeepFreezeGracePolicy
{
    public static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromSeconds(30);

    public TimeSpan GracePeriod { get; }

    public DeepFreezeGracePolicy(TimeSpan? customGracePeriod = null)
    {
        GracePeriod = customGracePeriod ?? DefaultGracePeriod;
    }

    /// <summary>
    /// Determines whether the elapsed pause duration qualifies for full Deep Freeze.
    /// </summary>
    /// <param name="pauseStartTimeUtc">The UTC timestamp when pause began.</param>
    /// <param name="currentTimeUtc">Current UTC timestamp.</param>
    /// <param name="isImmediateTrigger">True for battery saver, session lock, or display off which bypass grace delay.</param>
    public bool IsEligibleForDeepFreeze(
        DateTimeOffset pauseStartTimeUtc,
        DateTimeOffset currentTimeUtc,
        bool isImmediateTrigger)
    {
        if (isImmediateTrigger)
        {
            return true;
        }

        var elapsed = currentTimeUtc - pauseStartTimeUtc;
        return elapsed >= GracePeriod;
    }

    /// <summary>
    /// Calculates remaining time before deep freeze triggers. Returns <see cref="TimeSpan.Zero"/> if already eligible.
    /// </summary>
    public TimeSpan GetRemainingGrace(
        DateTimeOffset pauseStartTimeUtc,
        DateTimeOffset currentTimeUtc,
        bool isImmediateTrigger)
    {
        if (isImmediateTrigger)
        {
            return TimeSpan.Zero;
        }

        var elapsed = currentTimeUtc - pauseStartTimeUtc;
        var remaining = GracePeriod - elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }
}
