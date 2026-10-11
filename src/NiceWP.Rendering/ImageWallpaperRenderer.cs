using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NiceWP.Core;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace NiceWP.Rendering;

public sealed class ImageWallpaperRenderer : IWallpaperRenderer
{
    private Window? window;
    private AppWindow? appWindow;
    private Grid? viewport;
    private ImageBrush? imageBrush;
    private SoftwareBitmapSource? source;
    private bool disposed;

    public nint WindowHandle => window == null ? 0 : WinRT.Interop.WindowNative.GetWindowHandle(window);

    public async Task<WallpaperResult> LoadAsync(string path)
    {
        if (disposed || window != null)
            return new(WallpaperError.RendererFailed, "Renderer cannot be loaded again.");
        var validation = ImageWallpaper.Validate(path, out var fullPath);
        if (!validation.Success) return validation;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(fullPath);
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var codec = decoder.DecoderInformation.CodecId;
            if (codec != BitmapDecoder.PngDecoderId && codec != BitmapDecoder.JpegDecoderId)
                return new(WallpaperError.UnsupportedFormat, "The file content must be PNG or JPEG.");
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(bitmap);
        }
        catch (Exception exception)
        {
            Trace.WriteLine(exception);
            source?.Dispose();
            source = null;
            return new(WallpaperError.InvalidImage, "The image could not be read or decoded.");
        }

        try
        {
            imageBrush = new ImageBrush
            {
                ImageSource = source,
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            };
            // A background does not contribute the image's natural size to layout.
            // The root Grid is arranged to the client viewport, including after resize.
            viewport = new Grid
            {
                Background = imageBrush,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false
            };
            window = new Window { Title = "NiceWP Image Wallpaper", Content = viewport };
            // AppWindow lookup is only supported while the HWND is top-level.
            appWindow = window.AppWindow;
            appWindow.IsShownInSwitchers = false;
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
            }
            appWindow.MoveAndResize(DisplayArea.Primary.OuterBounds);
            Trace.WriteLine($"Image loaded: {fullPath}, window=0x{WindowHandle:X}");
            return new();
        }
        catch (Exception exception)
        {
            Trace.WriteLine(exception);
            return new(WallpaperError.RendererFailed, "The wallpaper window could not be created.");
        }
    }

    public void Show() => appWindow!.Show(false);

    public void Pause()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    public void Resume()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    public void Dispose()
    {
        if (disposed) return;
        if (imageBrush != null) imageBrush.ImageSource = null;
        if (viewport != null) viewport.Background = null;
        source?.Dispose();
        source = null;
        if (window != null)
        {
            window.Content = null;
            window.Close();
            window = null;
        }
        appWindow = null;
        imageBrush = null;
        viewport = null;
        disposed = true;
        Trace.WriteLine("Image renderer disposed; source and window released.");
    }
}
