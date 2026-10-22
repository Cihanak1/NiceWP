namespace NiceWP.Core;

/// <summary>
/// Geometric and kinetic parameters describing an active corner curl gesture.
/// </summary>
public sealed record CornerGestureContext(
    double StartX,
    double StartY,
    double CurrentX,
    double CurrentY,
    double ScreenWidth,
    double ScreenHeight,
    double Progress,
    double Velocity,
    TransitionSensitivity Sensitivity,
    TransitionQuality Quality,
    bool PaperWaveEnabled,
    double TopFoldX = 0.0,
    double RightFoldY = 0.0,
    double FoldAngleDegrees = 45.0)
{
    public const double DefaultCommitThreshold = 0.30;

    /// <summary>
    /// Gets whether the current progress satisfies the threshold to commit navigation to the next wallpaper.
    /// </summary>
    public bool CanCommit => Progress >= DefaultCommitThreshold;

    /// <summary>
    /// Computes the dynamic paper wave offset (in pixels) based on progress, pointer velocity, and quality.
    /// Returns 0 when paper wave is disabled or when at rest.
    /// </summary>
    public double ComputeWaveOffset()
    {
        if (!PaperWaveEnabled || Quality == TransitionQuality.Low || Progress <= 0.001 || Progress >= 0.999)
        {
            return 0.0;
        }

        // Wave peaks around mid-curl (progress ~ 0.5) and scales with velocity
        var progressCurve = Math.Sin(Progress * Math.PI);
        var velocityFactor = Math.Clamp(Math.Abs(Velocity) / 1200.0, 0.2, 1.8);
        var qualityScale = Quality == TransitionQuality.High ? 1.0 : 0.6;

        return progressCurve * velocityFactor * 14.0 * qualityScale;
    }

    /// <summary>
    /// Computes the curl shadow opacity based on progress and quality.
    /// </summary>
    public double ComputeShadowOpacity()
    {
        if (Quality == TransitionQuality.Low)
        {
            return Math.Clamp(Progress * 0.35, 0.0, 0.35);
        }

        var maxShadow = Quality == TransitionQuality.High ? 0.65 : 0.50;
        return Math.Clamp(Progress * maxShadow * 1.5, 0.0, maxShadow);
    }
}
