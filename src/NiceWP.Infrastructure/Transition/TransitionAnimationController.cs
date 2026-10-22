using System.Diagnostics;

namespace NiceWP.Infrastructure.Transition;

/// <summary>
/// Controls easing animations and threshold evaluations for committing or cancelling
/// page-turn transitions smoothly.
/// </summary>
public sealed class TransitionAnimationController
{
    public const double CommitThreshold = 0.30;
    public static readonly TimeSpan DefaultCancelDuration = TimeSpan.FromMilliseconds(220);
    public static readonly TimeSpan DefaultCommitDuration = TimeSpan.FromMilliseconds(280);

    /// <summary>
    /// Evaluates whether the given progress satisfies the threshold to commit navigation.
    /// </summary>
    public static bool ShouldCommit(double progress) => progress >= CommitThreshold;

    /// <summary>
    /// Applies cubic ease-out curve (1 - (1 - t)^3).
    /// </summary>
    public static double EaseOutCubic(double t)
    {
        var clamped = Math.Clamp(t, 0.0, 1.0);
        var inv = 1.0 - clamped;
        return 1.0 - (inv * inv * inv);
    }

    /// <summary>
    /// Applies cubic ease-in-out curve.
    /// </summary>
    public static double EaseInOutCubic(double t)
    {
        var clamped = Math.Clamp(t, 0.0, 1.0);
        return clamped < 0.5
            ? 4.0 * clamped * clamped * clamped
            : 1.0 - (Math.Pow((-2.0 * clamped) + 2.0, 3) / 2.0);
    }

    /// <summary>
    /// Asynchronously animates progress from a starting value to an end value using cubic ease-out.
    /// </summary>
    public async Task AnimateProgressAsync(
        double fromProgress,
        double toProgress,
        TimeSpan duration,
        Action<double> onStep,
        int stepIntervalMs = 16,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onStep);

        if (duration <= TimeSpan.Zero || Math.Abs(fromProgress - toProgress) < 0.001)
        {
            onStep(toProgress);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var totalMs = duration.TotalMilliseconds;
        var delta = toProgress - fromProgress;

        while (stopwatch.ElapsedMilliseconds < totalMs)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var normalizedTime = stopwatch.ElapsedMilliseconds / totalMs;
            var easedT = EaseOutCubic(normalizedTime);
            var currentProgress = Math.Clamp(fromProgress + (delta * easedT), 0.0, 1.0);

            onStep(currentProgress);

            await Task.Delay(Math.Max(10, stepIntervalMs), cancellationToken).ConfigureAwait(false);
        }

        onStep(toProgress);
    }
}
