using System.Diagnostics;

namespace NiceWP.Infrastructure.Transition;

/// <summary>
/// Provides geometric hit-testing logic for the top-right interactive hot corner.
/// </summary>
public static class HotCornerHitTester
{
    public const double DefaultCornerSize = 32.0;
    public const double MinCornerSize = 16.0;
    public const double MaxCornerSize = 64.0;

    /// <summary>
    /// Checks whether the specified pointer position falls inside the top-right hot corner.
    /// </summary>
    public static bool IsPointInTopRightCorner(
        double x,
        double y,
        double screenWidth,
        double screenHeight,
        double cornerSize = DefaultCornerSize)
    {
        if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y))
        {
            return false;
        }

        if (screenWidth <= 0 || screenHeight <= 0)
        {
            return false;
        }

        var clampedSize = Math.Clamp(cornerSize, MinCornerSize, MaxCornerSize);
        var cornerLeft = screenWidth - clampedSize;

        return x >= cornerLeft && x <= screenWidth && y >= 0.0 && y <= clampedSize;
    }

    /// <summary>
    /// Returns the bounding box of the top-right hot corner.
    /// </summary>
    public static (double X, double Y, double Width, double Height) GetTopRightCornerRect(
        double screenWidth,
        double cornerSize = DefaultCornerSize)
    {
        var clampedSize = Math.Clamp(cornerSize, MinCornerSize, MaxCornerSize);
        var x = Math.Max(0.0, screenWidth - clampedSize);
        return (x, 0.0, clampedSize, clampedSize);
    }

    /// <summary>
    /// Calculates the normalized distance of the pointer from the top-right apex (0.0 = exact corner, 1.0 = boundary).
    /// </summary>
    public static double CalculateNormalizedCornerDistance(
        double x,
        double y,
        double screenWidth,
        double cornerSize = DefaultCornerSize)
    {
        var clampedSize = Math.Clamp(cornerSize, MinCornerSize, MaxCornerSize);
        var dx = Math.Max(0.0, screenWidth - x);
        var dy = Math.Max(0.0, y);
        var distance = Math.Sqrt((dx * dx) + (dy * dy));
        var maxDistance = Math.Sqrt(2) * clampedSize;

        return Math.Clamp(distance / maxDistance, 0.0, 1.0);
    }
}

/// <summary>
/// Monitors pointer coordinates relative to the screen and raises hover lifecycle events
/// for the top-right hot corner.
/// </summary>
public sealed class HotCornerDetector
{
    private readonly Action<string, string>? _auditLogger;
    private double _screenWidth;
    private double _screenHeight;
    private double _cornerSize = HotCornerHitTester.DefaultCornerSize;
    private bool _isEnabled = true;
    private bool _isHovered;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            if (!_isEnabled && _isHovered)
            {
                SetHovered(false);
            }
        }
    }

    public bool IsHovered => _isHovered;

    public double ScreenWidth => _screenWidth;

    public double ScreenHeight => _screenHeight;

    public double CornerSize
    {
        get => _cornerSize;
        set => _cornerSize = Math.Clamp(value, HotCornerHitTester.MinCornerSize, HotCornerHitTester.MaxCornerSize);
    }

    public event Action? CornerEntered;
    public event Action? CornerExited;
    public event Action<bool>? HoverStateChanged;

    public HotCornerDetector(
        double screenWidth = 1920,
        double screenHeight = 1080,
        double cornerSize = HotCornerHitTester.DefaultCornerSize,
        Action<string, string>? auditLogger = null)
    {
        _screenWidth = Math.Max(1, screenWidth);
        _screenHeight = Math.Max(1, screenHeight);
        _cornerSize = Math.Clamp(cornerSize, HotCornerHitTester.MinCornerSize, HotCornerHitTester.MaxCornerSize);
        _auditLogger = auditLogger;
    }

    /// <summary>
    /// Updates screen dimensions (e.g. on resolution or DPI change).
    /// </summary>
    public void UpdateScreenDimensions(double width, double height)
    {
        if (width <= 0 || height <= 0) return;
        _screenWidth = width;
        _screenHeight = height;
    }

    /// <summary>
    /// Evaluates pointer position and updates hover state accordingly.
    /// </summary>
    public bool ProcessPointerMoved(double x, double y)
    {
        if (!_isEnabled)
        {
            if (_isHovered)
            {
                SetHovered(false);
                return true;
            }
            return false;
        }

        var isInside = HotCornerHitTester.IsPointInTopRightCorner(x, y, _screenWidth, _screenHeight, _cornerSize);
        if (isInside != _isHovered)
        {
            SetHovered(isInside);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Forces hover state to reset (e.g. when pointer leaves screen or window loses focus).
    /// </summary>
    public void Reset()
    {
        if (_isHovered)
        {
            SetHovered(false);
        }
    }

    private void SetHovered(bool hovered)
    {
        _isHovered = hovered;
        if (hovered)
        {
            _auditLogger?.Invoke("HotCornerEntered", $"Pointer entered top-right hot corner (size={_cornerSize}px).");
            Trace.WriteLine("[HotCornerDetector] Pointer entered top-right hot corner.");
            CornerEntered?.Invoke();
        }
        else
        {
            _auditLogger?.Invoke("HotCornerExited", "Pointer exited top-right hot corner.");
            Trace.WriteLine("[HotCornerDetector] Pointer exited top-right hot corner.");
            CornerExited?.Invoke();
        }

        HoverStateChanged?.Invoke(_isHovered);
    }
}
