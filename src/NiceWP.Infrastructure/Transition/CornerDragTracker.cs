namespace NiceWP.Infrastructure.Transition;

/// <summary>
/// Geometric and kinetic metrics of an active corner page-turn drag.
/// </summary>
public sealed record DragMetrics(
    double Progress,
    double DiagonalDistance,
    double Velocity,
    double SmoothedVelocity,
    double TopFoldX,
    double RightFoldY,
    double FoldAngleDegrees);

/// <summary>
/// Calculates directional diagonal drag progress, fold line intersections,
/// and smoothed velocity for the interactive page-turn gesture.
/// </summary>
public sealed class CornerDragTracker
{
    private const double Sqrt2Over2 = 0.7071067811865475;
    private const double VelocitySmoothingFactor = 0.25;

    private readonly double _startX;
    private readonly double _startY;
    private readonly double _screenWidth;
    private readonly double _screenHeight;
    private readonly double _thresholdDistance;

    private double _smoothedVelocity;

    public double StartX => _startX;
    public double StartY => _startY;
    public double ScreenWidth => _screenWidth;
    public double ScreenHeight => _screenHeight;
    public double ThresholdDistance => _thresholdDistance;

    public CornerDragTracker(
        double startX,
        double startY,
        double screenWidth,
        double screenHeight,
        double thresholdDistance = 360.0)
    {
        _startX = startX;
        _startY = startY;
        _screenWidth = Math.Max(1.0, screenWidth);
        _screenHeight = Math.Max(1.0, screenHeight);
        _thresholdDistance = Math.Max(50.0, thresholdDistance);
    }

    /// <summary>
    /// Computes full drag metrics for a pointer position and instantaneous velocity.
    /// </summary>
    public DragMetrics TrackPosition(double currentX, double currentY, double rawVelocity = 0.0)
    {
        var dx = _startX - currentX; // Positive when moving left
        var dy = currentY - _startY; // Positive when moving down

        // Project onto diagonal (down-left)
        var diagonalDistance = Math.Max(0.0, (dx * Sqrt2Over2) + (dy * Sqrt2Over2));
        var progress = Math.Clamp(diagonalDistance / _thresholdDistance, 0.0, 1.0);

        // Exponential smoothing for velocity
        _smoothedVelocity = (_smoothedVelocity * (1.0 - VelocitySmoothingFactor)) + (Math.Max(0.0, rawVelocity) * VelocitySmoothingFactor);

        // Fold line geometry:
        // Top-right corner is (ScreenWidth, 0)
        var clampedX = Math.Clamp(currentX, 0.0, _screenWidth);
        var clampedY = Math.Clamp(currentY, 0.0, _screenHeight);

        var deltaX = _screenWidth - clampedX;
        var deltaY = clampedY;

        double topFoldX;
        double rightFoldY;
        double foldAngleDegrees = 45.0;

        if (deltaX > 0.001 || deltaY > 0.001)
        {
            var midX = (_screenWidth + clampedX) / 2.0;
            var midY = clampedY / 2.0;

            if (deltaX > 0.001)
            {
                // Top edge (Y = 0)
                topFoldX = Math.Clamp(midX - ((deltaY * deltaY) / (2.0 * deltaX)), 0.0, _screenWidth);
            }
            else
            {
                topFoldX = _screenWidth;
            }

            if (deltaY > 0.001)
            {
                // Right edge (X = ScreenWidth)
                rightFoldY = Math.Clamp(midY + ((deltaX * deltaX) / (2.0 * deltaY)), 0.0, _screenHeight);
            }
            else
            {
                rightFoldY = 0.0;
            }

            foldAngleDegrees = Math.Atan2(deltaY, deltaX) * (180.0 / Math.PI);
        }
        else
        {
            topFoldX = _screenWidth;
            rightFoldY = 0.0;
        }

        return new DragMetrics(
            Progress: progress,
            DiagonalDistance: diagonalDistance,
            Velocity: rawVelocity,
            SmoothedVelocity: _smoothedVelocity,
            TopFoldX: topFoldX,
            RightFoldY: rightFoldY,
            FoldAngleDegrees: foldAngleDegrees);
    }
}
