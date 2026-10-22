using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NiceWP.Core;
using Windows.Foundation;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace NiceWP.Rendering;

/// <summary>
/// Renders the visual page-turn transition surface, displaying the curling paper flap
/// and revealing the next wallpaper underneath.
/// </summary>
public sealed class PageTurnSurfaceRenderer
{
    private readonly Grid _rootGrid;
    private readonly Canvas _renderCanvas;
    private readonly Image _currentWallpaperImage;
    private readonly ImageBrush _nextWallpaperBrush;
    private readonly XamlPath _revealedUnderneathPath;
    private readonly XamlPath _peeledFlapPath;

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

        _peeledFlapPath = new XamlPath
        {
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(240, 245, 245, 250)),
            Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(120, 180, 180, 190)),
            StrokeThickness = 1.0
        };

        _renderCanvas.Children.Add(_revealedUnderneathPath);
        _renderCanvas.Children.Add(_peeledFlapPath);

        // Layer order: Current wallpaper (base) -> Revealed Path with next wallpaper brush -> Curling flap / overlays
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

        // 1. Reveal the next wallpaper underneath the peeled top-right region
        UpdateRevealedUnderneath(w, topFoldX, rightFoldY);

        // 2. Render the curling flap (paper back)
        UpdateCurlingFlap(w, h, topFoldX, rightFoldY, apexX, apexY);
    }

    /// <summary>
    /// Resets the surface to idle state and hides the overlay.
    /// </summary>
    public void Reset()
    {
        _revealedUnderneathPath.Data = null;
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

        // Revealed triangle in top right: (topFoldX, 0) -> (w, 0) -> (w, rightFoldY)
        figure.Segments.Add(new LineSegment { Point = new Point(w, 0) });
        figure.Segments.Add(new LineSegment { Point = new Point(w, rightFoldY) });

        geometry.Figures.Add(figure);
        _revealedUnderneathPath.Data = geometry;
    }

    private void UpdateCurlingFlap(double w, double h, double topFoldX, double rightFoldY, double apexX, double apexY)
    {
        var flapGeometry = new PathGeometry();
        var flapFigure = new PathFigure
        {
            StartPoint = new Point(topFoldX, 0),
            IsClosed = true,
            IsFilled = true
        };

        // Flap triangle: (topFoldX, 0) -> Apex (apexX, apexY) -> (w, rightFoldY)
        flapFigure.Segments.Add(new LineSegment { Point = new Point(apexX, apexY) });
        flapFigure.Segments.Add(new LineSegment { Point = new Point(w, rightFoldY) });

        flapGeometry.Figures.Add(flapFigure);
        _peeledFlapPath.Data = flapGeometry;
    }
}
