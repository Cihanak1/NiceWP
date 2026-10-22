using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NiceWP.Core;
using Windows.Foundation;
using Windows.UI;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace NiceWP.Rendering;

/// <summary>
/// Renders the visual page-turn transition surface, displaying the curling paper flap,
/// realistic shadow, and crease depth while revealing the next wallpaper underneath.
/// </summary>
public sealed class PageTurnSurfaceRenderer
{
    private readonly Grid _rootGrid;
    private readonly Canvas _renderCanvas;
    private readonly Image _currentWallpaperImage;
    private readonly ImageBrush _nextWallpaperBrush;
    private readonly XamlPath _revealedUnderneathPath;
    private readonly XamlPath _foldShadowPath;
    private readonly XamlPath _peeledFlapPath;
    private readonly LinearGradientBrush _foldShadowBrush;
    private readonly LinearGradientBrush _flapLightingBrush;

    private double _screenWidth = 1920;
    private double _screenHeight = 1080;
    private bool _isVisible;

    public FrameworkElement SurfaceElement => _rootGrid;

    public bool IsVisible => _isVisible;

    public PageTurnSurfaceRenderer()
    {
        _rootGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Visibility = Visibility.Collapsed
        };

        _currentWallpaperImage = new Image
        {
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        _renderCanvas = new Canvas
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        _nextWallpaperBrush = new ImageBrush
        {
            Stretch = Stretch.UniformToFill,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top
        };

        _revealedUnderneathPath = new XamlPath
        {
            Fill = _nextWallpaperBrush
        };

        // Fold shadow cast onto revealed area underneath
        _foldShadowBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        _foldShadowBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(120, 15, 18, 24), Offset = 0.0 });
        _foldShadowBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, 15, 18, 24), Offset = 1.0 });

        _foldShadowPath = new XamlPath
        {
            Fill = _foldShadowBrush
        };

        // Flap paper lighting gradient giving cylindrical paper volume
        _flapLightingBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        _flapLightingBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 215, 220, 228), Offset = 0.0 }); // Crease shadow
        _flapLightingBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 255, 255, 255), Offset = 0.20 }); // Specular ridge highlight
        _flapLightingBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 245, 247, 250), Offset = 0.65 }); // Paper body
        _flapLightingBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 230, 234, 240), Offset = 1.0 }); // Curl edge

        _peeledFlapPath = new XamlPath
        {
            Fill = _flapLightingBrush,
            Stroke = new SolidColorBrush(Color.FromArgb(90, 160, 170, 185)),
            StrokeThickness = 0.8
        };

        _renderCanvas.Children.Add(_revealedUnderneathPath);
        _renderCanvas.Children.Add(_foldShadowPath);
        _renderCanvas.Children.Add(_peeledFlapPath);

        // Layer order: Current wallpaper (base) -> RenderCanvas (revealed, shadow, flap)
        _rootGrid.Children.Add(_currentWallpaperImage);
        _rootGrid.Children.Add(_renderCanvas);
    }

    /// <summary>
    /// Configures the viewport dimensions.
    /// </summary>
    public void SetViewportSize(double width, double height)
    {
        _screenWidth = Math.Max(1.0, width);
        _screenHeight = Math.Max(1.0, height);
        _rootGrid.Width = _screenWidth;
        _rootGrid.Height = _screenHeight;
    }

    /// <summary>
    /// Updates the image sources for current active wallpaper and next candidate wallpaper.
    /// </summary>
    public void SetWallpapers(ImageSource? currentSource, ImageSource? nextSource)
    {
        _currentWallpaperImage.Source = currentSource;
        _nextWallpaperBrush.ImageSource = nextSource;
    }

    /// <summary>
    /// Renders a single frame of the page-turn effect according to the active gesture context.
    /// </summary>
    public void RenderTurn(CornerGestureContext context)
    {
        if (context.Progress <= 0.001)
        {
            Reset();
            return;
        }

        if (!_isVisible)
        {
            _rootGrid.Visibility = Visibility.Visible;
            _isVisible = true;
        }

        var w = _screenWidth;
        var h = _screenHeight;

        var topFoldX = Math.Clamp(context.TopFoldX > 0 ? context.TopFoldX : w - (context.Progress * 300), 0.0, w);
        var rightFoldY = Math.Clamp(context.RightFoldY > 0 ? context.RightFoldY : context.Progress * 300, 0.0, h);
        var apexX = Math.Clamp(context.CurrentX, 0.0, w);
        var apexY = Math.Clamp(context.CurrentY, 0.0, h);

        // 1. Reveal next wallpaper underneath the peeled top-right region
        UpdateRevealedUnderneath(w, topFoldX, rightFoldY);

        // 2. Render fold cast shadow
        UpdateFoldShadow(w, topFoldX, rightFoldY, context);

        // 3. Render curling paper flap with depth lighting
        UpdateCurlingFlap(w, h, topFoldX, rightFoldY, apexX, apexY, context);
    }

    /// <summary>
    /// Resets the surface to idle state and hides the overlay.
    /// </summary>
    public void Reset()
    {
        _revealedUnderneathPath.Data = null;
        _foldShadowPath.Data = null;
        _peeledFlapPath.Data = null;
        _rootGrid.Visibility = Visibility.Collapsed;
        _isVisible = false;
    }

    private void UpdateRevealedUnderneath(double w, double topFoldX, double rightFoldY)
    {
        var geometry = new PathGeometry();
        var figure = new PathFigure
        {
            StartPoint = new Point(topFoldX, 0),
            IsClosed = true,
            IsFilled = true
        };

        figure.Segments.Add(new LineSegment { Point = new Point(w, 0) });
        figure.Segments.Add(new LineSegment { Point = new Point(w, rightFoldY) });

        geometry.Figures.Add(figure);
        _revealedUnderneathPath.Data = geometry;
    }

    private void UpdateFoldShadow(double w, double topFoldX, double rightFoldY, CornerGestureContext context)
    {
        var shadowOpacity = context.ComputeShadowOpacity();
        if (shadowOpacity <= 0.01)
        {
            _foldShadowPath.Data = null;
            return;
        }

        // Modulate gradient stop opacity
        var alpha = (byte)Math.Clamp(shadowOpacity * 255.0, 0, 255);
        _foldShadowBrush.GradientStops[0].Color = Color.FromArgb(alpha, 15, 18, 24);

        var dx = w - topFoldX;
        var dy = rightFoldY;
        var len = Math.Sqrt((dx * dx) + (dy * dy));

        if (len < 1.0)
        {
            _foldShadowPath.Data = null;
            return;
        }

        // Perpendicular unit vector pointing towards top-right corner
        var nx = dy / len;
        var ny = -dx / len;
        var shadowWidth = Math.Clamp(context.Progress * 36.0, 8.0, 36.0);

        var pA = new Point(topFoldX, 0);
        var pB = new Point(w, rightFoldY);
        var pBShadow = new Point(Math.Min(w, pB.X + (nx * shadowWidth)), pB.Y + (ny * shadowWidth));
        var pAShadow = new Point(Math.Min(w, pA.X + (nx * shadowWidth)), Math.Max(0, pA.Y + (ny * shadowWidth)));

        var shadowGeometry = new PathGeometry();
        var shadowFigure = new PathFigure
        {
            StartPoint = pA,
            IsClosed = true,
            IsFilled = true
        };

        shadowFigure.Segments.Add(new LineSegment { Point = pB });
        shadowFigure.Segments.Add(new LineSegment { Point = pBShadow });
        shadowFigure.Segments.Add(new LineSegment { Point = pAShadow });

        shadowGeometry.Figures.Add(shadowFigure);
        _foldShadowPath.Data = shadowGeometry;

        // Align shadow gradient perpendicular to the fold line
        _foldShadowBrush.StartPoint = new Point((pA.X + pB.X) / (2.0 * _screenWidth), (pA.Y + pB.Y) / (2.0 * _screenHeight));
        _foldShadowBrush.EndPoint = new Point((pAShadow.X + pBShadow.X) / (2.0 * _screenWidth), (pAShadow.Y + pBShadow.Y) / (2.0 * _screenHeight));
    }

    private void UpdateCurlingFlap(
        double w,
        double h,
        double topFoldX,
        double rightFoldY,
        double apexX,
        double apexY,
        CornerGestureContext context)
    {
        var flapGeometry = new PathGeometry();
        var flapFigure = new PathFigure
        {
            StartPoint = new Point(topFoldX, 0),
            IsClosed = true,
            IsFilled = true
        };

        var waveOffset = context.ComputeWaveOffset();

        if (Math.Abs(waveOffset) > 0.05)
        {
            // Segment 1: from (topFoldX, 0) to Apex (apexX, apexY) with wave curve
            var dx1 = apexX - topFoldX;
            var dy1 = apexY - 0;
            var len1 = Math.Max(1.0, Math.Sqrt((dx1 * dx1) + (dy1 * dy1)));
            var nx1 = -dy1 / len1;
            var ny1 = dx1 / len1;
            var mid1X = (topFoldX + apexX) / 2.0;
            var mid1Y = apexY / 2.0;
            var ctrl1 = new Point(mid1X + (nx1 * waveOffset), mid1Y + (ny1 * waveOffset));

            flapFigure.Segments.Add(new QuadraticBezierSegment
            {
                Point1 = ctrl1,
                Point2 = new Point(apexX, apexY)
            });

            // Segment 2: from Apex (apexX, apexY) to (w, rightFoldY) with subtle complementary wave
            var dx2 = w - apexX;
            var dy2 = rightFoldY - apexY;
            var len2 = Math.Max(1.0, Math.Sqrt((dx2 * dx2) + (dy2 * dy2)));
            var nx2 = -dy2 / len2;
            var ny2 = dx2 / len2;
            var mid2X = (apexX + w) / 2.0;
            var mid2Y = (apexY + rightFoldY) / 2.0;
            var ctrl2 = new Point(mid2X + (nx2 * waveOffset * 0.6), mid2Y + (ny2 * waveOffset * 0.6));

            flapFigure.Segments.Add(new QuadraticBezierSegment
            {
                Point1 = ctrl2,
                Point2 = new Point(w, rightFoldY)
            });
        }
        else
        {
            // Straight fold edges when at rest or when paper wave is disabled
            flapFigure.Segments.Add(new LineSegment { Point = new Point(apexX, apexY) });
            flapFigure.Segments.Add(new LineSegment { Point = new Point(w, rightFoldY) });
        }

        flapGeometry.Figures.Add(flapFigure);
        _peeledFlapPath.Data = flapGeometry;

        // Dynamic lighting vector along fold angle
        var rad = context.FoldAngleDegrees * (Math.PI / 180.0);
        _flapLightingBrush.StartPoint = new Point(topFoldX / _screenWidth, 0);
        _flapLightingBrush.EndPoint = new Point(
            Math.Clamp((topFoldX / _screenWidth) + (Math.Cos(rad) * 0.2), 0.0, 1.0),
            Math.Clamp(Math.Sin(rad) * 0.2, 0.0, 1.0));
    }
}
