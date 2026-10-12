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
    private nint windowHandle;
    private AppWindow? appWindow;
    private Grid? viewport;
    private ImageBrush? imageBrush;
    private SoftwareBitmapSource? source;
    private bool disposed;

    public nint WindowHandle =>
        window != null && windowHandle != 0 && RendererNativeMethods.IsWindow(windowHandle) ? windowHandle : 0;

    public async Task<WallpaperResult> LoadAsync(string path)
    {
        if (disposed || window != null)
            return new(WallpaperError.RendererFailed, "Renderer cannot be loaded again.");
        var validation = ImageWallpaper.Validate(path, out var fullPath);
        if (!validation.Success) return validation;

        Windows.Graphics.RectInt32 targetBounds;
        try
        {
            targetBounds = DisplayArea.Primary.OuterBounds;
        }
        catch (Exception exception)
        {
            Trace.WriteLine(exception);
            return new(WallpaperError.RendererFailed, "The wallpaper window could not be created.");
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(fullPath);
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var codec = decoder.DecoderInformation.CodecId;
            if (codec != BitmapDecoder.PngDecoderId && codec != BitmapDecoder.JpegDecoderId)
                return new(WallpaperError.UnsupportedFormat, "The file content must be PNG or JPEG.");
            using var bitmap = await DecodeBitmapAsync(decoder, targetBounds.Width, targetBounds.Height);
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
            windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            // AppWindow lookup is only supported while the HWND is top-level.
            appWindow = window.AppWindow;
            appWindow.IsShownInSwitchers = false;
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
            }
            appWindow.MoveAndResize(targetBounds);
            if (WindowHandle == 0)
            {
                Dispose();
                return new(WallpaperError.RendererFailed, "The wallpaper window could not be created.");
            }
            Trace.WriteLine($"Image loaded: {fullPath}, window=0x{WindowHandle:X}");
            return new();
        }
        catch (Exception exception)
        {
            Trace.WriteLine(exception);
            Dispose();
            return new(WallpaperError.RendererFailed, "The wallpaper window could not be created.");
        }
    }

    public static async Task<SoftwareBitmap> DecodeBitmapAsync(BitmapDecoder decoder, int targetWidth, int targetHeight)
    {
        ArgumentNullException.ThrowIfNull(decoder);

        var rawWidth = decoder.PixelWidth;
        var rawHeight = decoder.PixelHeight;
        var isTransposed = decoder.OrientedPixelWidth == rawHeight &&
                           decoder.OrientedPixelHeight == rawWidth &&
                           rawWidth != rawHeight;

        var effectiveTargetWidth = isTransposed ? targetHeight : targetWidth;
        var effectiveTargetHeight = isTransposed ? targetWidth : targetHeight;
        var (decodeWidth, decodeHeight) = ComputeDecodeDimensions(rawWidth, rawHeight, effectiveTargetWidth, effectiveTargetHeight);

        if (decodeWidth < rawWidth && decodeHeight < ImageWallpaperMaxDimension(rawHeight))
        {
            var transform = new BitmapTransform
            {
                ScaledWidth = decodeWidth,
                ScaledHeight = decodeHeight,
                InterpolationMode = BitmapInterpolationMode.Linear
            };

            return await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);
        }

        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }

    private static uint ImageWallpaperMaxDimension(uint rawHeight) => rawHeight;

    public static (uint Width, uint Height) ComputeDecodeDimensions(
        uint sourceWidth,
        uint sourceHeight,
        int targetWidth,
        int targetHeight)
    {
        if (sourceWidth == 0 || sourceHeight == 0 || targetWidth <= 0 || targetHeight <= 0)
        {
            return (sourceWidth, sourceHeight);
        }

        if (sourceWidth <= targetWidth || sourceHeight <= targetHeight)
        {
            return (sourceWidth, sourceHeight);
        }

        var scale = Math.Max((double)targetWidth / sourceWidth, (double)targetHeight / sourceHeight);
        if (scale >= 1.0)
        {
            return (sourceWidth, sourceHeight);
        }

        var scaledWidth = (uint)Math.Clamp(Math.Round(sourceWidth * scale), targetWidth, sourceWidth);
        var scaledHeight = (uint)Math.Clamp(Math.Round(sourceHeight * scale), targetHeight, sourceHeight);
        return (scaledWidth, scaledHeight);
    }

    public void Show()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (WindowHandle == 0) throw new InvalidOperationException("The image wallpaper window is no longer valid.");
        appWindow!.Show(false);
    }

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
        disposed = true;

        var currentWindow = window;
        var currentHandle = windowHandle;
        var currentBrush = imageBrush;
        var currentViewport = viewport;
        var currentSource = source;

        window = null;
        windowHandle = 0;
        appWindow = null;
        imageBrush = null;
        viewport = null;
        source = null;

        var hwndAlive = currentWindow != null && currentHandle != 0 && RendererNativeMethods.IsWindow(currentHandle);
        if (hwndAlive)
        {
            try
            {
                if (currentBrush != null) currentBrush.ImageSource = null;
                if (currentViewport != null) currentViewport.Background = null;
            }
            catch (Exception exception)
            {
                Trace.WriteLine(exception);
            }
        }

        try
        {
            currentSource?.Dispose();
        }
        catch (Exception exception)
        {
            Trace.WriteLine(exception);
        }

        if (currentWindow != null && hwndAlive && RendererNativeMethods.IsWindow(currentHandle))
        {
            try
            {
                currentWindow.Content = null;
                currentWindow.Close();
            }
            catch (Exception exception)
            {
                Trace.WriteLine(exception);
            }
        }
        else if (currentHandle != 0)
        {
            Trace.WriteLine($"Image window 0x{currentHandle:X} already destroyed; skipped XAML Window teardown.");
        }

        Trace.WriteLine("Image renderer disposed; source and window released.");
    }
}
