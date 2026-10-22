using System.Diagnostics;
using System.Runtime.InteropServices;
using NiceWP.Core;
using NiceWP.Infrastructure.Library;

namespace NiceWP.Infrastructure.Transition;

/// <summary>
/// A dedicated, input-transparent Win32 layered visual window for rendering
/// page-curl transitions, corner hover peel preview, drop shadows, and next wallpaper candidate previews.
/// Configured with WS_EX_TRANSPARENT and WS_EX_LAYERED so it NEVER intercepts, captures,
/// or blocks mouse input, allowing Windows desktop icons and Explorer to operate 100% normally.
/// </summary>
public sealed class PageTurnVisualOverlay : IDisposable
{
    private const string WindowClassName = "NiceWP_PageTurnVisualOverlay";

    // Win32 Window Styles
    private const uint WsPopup = 0x80000000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExTopMost = 0x00000008;
    private const uint WsExNoActivate = 0x08000000;
    private const uint WsExLayered = 0x00080000;
    private const uint WsExTransparent = 0x00000020;

    private const int SwShowNoActivate = 4;
    private const int SwHide = 0;

    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoZOrder = 0x0004;
    private static readonly nint HwndTopMost = new(-1);

    private const uint UlwAlpha = 0x00000002;
    private const byte AcSrcOver = 0x00;
    private const byte AcSrcAlpha = 0x01;

    private nint _hWnd;
    private nint _gdiToken;
    private int _screenWidth;
    private int _screenHeight;
    private bool _isVisible;
    private int _renderedFrameCount;
    private bool _isDisposed;

    // Double-buffered DIB for layered window
    private nint _hdcMem;
    private nint _hBitmap;
    private nint _oldBitmap;
    private nint _pBits;
    private int _bufWidth;
    private int _bufHeight;

    // Dirty bounding region tracking for high-performance sub-region clearing
    private int _lastDirtyLeft;
    private int _lastDirtyTop;
    private int _lastDirtyRight;
    private int _lastDirtyBottom;
    private bool _hasDirtyRegion;

    // Cached GDI+ brushes and pens (eliminates 10-15 allocations per frame)
    private nint _brushShadow;
    private nint _brushRevealedBg;
    private nint _brushFlap;
    private nint _penFlapHighlight;
    private nint _penCrease;
    private nint _penEdge;
    private bool _gdiResourcesCreated;

    // Preloaded image handles for GDI+
    private nint _nextGdiImage;
    private string? _loadedNextImagePath;

    public nint Handle => _hWnd;
    public bool IsVisible => _isVisible;
    public int RenderedFrameCount => _renderedFrameCount;
    public int ScreenWidth => _screenWidth;
    public int ScreenHeight => _screenHeight;

    public PageTurnVisualOverlay()
    {
        InitializeScreenDimensions();
        InitializeGdiPlus();
        RegisterAndCreateWindow();
    }

    private void InitializeScreenDimensions()
    {
        _screenWidth = NativeMethods.GetSystemMetrics(0); // SM_CXSCREEN
        _screenHeight = NativeMethods.GetSystemMetrics(1); // SM_CYSCREEN
        if (_screenWidth <= 0) _screenWidth = 1920;
        if (_screenHeight <= 0) _screenHeight = 1080;
    }

    private void InitializeGdiPlus()
    {
        var input = GdiPlusNativeMethods.GdiplusStartupInput.Default;
        _ = GdiPlusNativeMethods.GdiplusStartup(out _gdiToken, in input, out _);
    }

    private void RegisterAndCreateWindow()
    {
        var hInstance = NativeMethods.GetModuleHandle(null);
        var wc = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate((NativeMethods.WndProc)NativeMethods.DefWindowProc),
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = hInstance,
            hIcon = 0,
            hCursor = NativeMethods.LoadCursor(0, 32512), // IDC_ARROW
            hbrBackground = 0,
            lpszMenuName = null,
            lpszClassName = WindowClassName,
            hIconSm = 0
        };

        _ = NativeMethods.RegisterClassEx(ref wc);

        // Created fullscreen with WS_EX_TRANSPARENT: completely click-through
        _hWnd = NativeMethods.CreateWindowEx(
            WsExToolWindow | WsExTopMost | WsExNoActivate | WsExLayered | WsExTransparent,
            WindowClassName,
            "NiceWP Page Turn Overlay",
            WsPopup,
            0, 0, _screenWidth, _screenHeight,
            0, 0, hInstance, 0);

        if (_hWnd != 0)
        {
            EnsureBuffer(_screenWidth, _screenHeight);
        }
    }

    /// <summary>
    /// Displays the subtle top-right corner lift hover preview.
    /// </summary>
    public void ShowHover(int? screenWidth = null, int? screenHeight = null)
    {
        if (_hWnd == 0 || _isDisposed) return;

        var w = screenWidth ?? _screenWidth;
        var h = screenHeight ?? _screenHeight;

        EnsureBuffer(w, h);
        RenderHoverSurface(w, h);

        NativeMethods.SetWindowPos(_hWnd, HwndTopMost, 0, 0, w, h, SwpNoActivate | SwpShowWindow);
        NativeMethods.ShowWindow(_hWnd, SwShowNoActivate);
        _isVisible = true;
    }

    /// <summary>
    /// Hides the hover preview.
    /// </summary>
    public void HideHover()
    {
        if (_hWnd == 0 || _isDisposed) return;
        Hide();
    }

    /// <summary>
    /// Renders an active drag frame reflecting the current gesture context.
    /// </summary>
    public void ShowDragFrame(CornerGestureContext context, string? nextImagePath = null, string? currentImagePath = null)
    {
        if (_hWnd == 0 || _isDisposed) return;

        var w = (int)context.ScreenWidth;
        var h = (int)context.ScreenHeight;
        if (w <= 0) w = _screenWidth;
        if (h <= 0) h = _screenHeight;

        EnsureBuffer(w, h);
        EnsureLoadedImages(nextImagePath, currentImagePath);

        RenderCurlFrame(context, w, h);

        if (!_isVisible)
        {
            NativeMethods.SetWindowPos(_hWnd, HwndTopMost, 0, 0, w, h, SwpNoActivate | SwpShowWindow);
            NativeMethods.ShowWindow(_hWnd, SwShowNoActivate);
            _isVisible = true;
        }
    }

    /// <summary>
    /// Animates completion of page turn to 100% progress.
    /// </summary>
    public async Task AnimateCommitAsync(
        CornerGestureContext startContext,
        string? nextImagePath = null,
        string? currentImagePath = null,
        CancellationToken cancellationToken = default)
    {
        if (_hWnd == 0 || _isDisposed) return;

        var steps = 10;
        var initialProgress = startContext.Progress;

        for (var i = 1; i <= steps; i++)
        {
            if (_isDisposed || cancellationToken.IsCancellationRequested) break;

            var t = (double)i / steps;
            var easedT = Math.Sin(t * Math.PI / 2.0); // Ease-out
            var progress = initialProgress + (1.0 - initialProgress) * easedT;

            var animatedContext = startContext with
            {
                Progress = progress,
                CurrentX = startContext.StartX - (startContext.ScreenWidth * 1.2 * progress),
                CurrentY = startContext.StartY + (startContext.ScreenHeight * 1.2 * progress)
            };

            ShowDragFrame(animatedContext, nextImagePath, currentImagePath);
            await Task.Delay(15, cancellationToken).ConfigureAwait(false);
        }

        Hide();
    }

    /// <summary>
    /// Animates spring-back cancellation to 0% progress.
    /// </summary>
    public async Task AnimateCancelAsync(
        CornerGestureContext startContext,
        string? nextImagePath = null,
        string? currentImagePath = null,
        CancellationToken cancellationToken = default)
    {
        if (_hWnd == 0 || _isDisposed) return;

        var steps = 8;
        var initialProgress = startContext.Progress;

        for (var i = 1; i <= steps; i++)
        {
            if (_isDisposed || cancellationToken.IsCancellationRequested) break;

            var t = (double)i / steps;
            var easedT = Math.Sin(t * Math.PI / 2.0); // Ease-out
            var progress = initialProgress * (1.0 - easedT);

            var animatedContext = startContext with
            {
                Progress = progress,
                CurrentX = startContext.StartX - (startContext.StartX - startContext.CurrentX) * (1.0 - easedT),
                CurrentY = startContext.StartY + (startContext.CurrentY - startContext.StartY) * (1.0 - easedT)
            };

            ShowDragFrame(animatedContext, nextImagePath, currentImagePath);
            await Task.Delay(15, cancellationToken).ConfigureAwait(false);
        }

        Hide();
    }

    /// <summary>
    /// Hides the overlay and clears buffers.
    /// </summary>
    public void Hide()
    {
        if (_hWnd == 0 || _isDisposed) return;

        NativeMethods.ShowWindow(_hWnd, SwHide);
        _isVisible = false;

        if (_pBits != 0 && _bufWidth > 0 && _bufHeight > 0)
        {
            NativeMethods.ZeroMemory(_pBits, (nuint)(_bufWidth * _bufHeight * 4));
            _hasDirtyRegion = false;
        }
    }

    private void EnsureGdiResources()
    {
        if (_gdiResourcesCreated) return;
        GdiPlusNativeMethods.GdipCreateSolidFill(0x88000000, out _brushShadow);
        GdiPlusNativeMethods.GdipCreateSolidFill(0xEE1E2430, out _brushRevealedBg);
        GdiPlusNativeMethods.GdipCreateSolidFill(0xFFECEFF4, out _brushFlap);
        GdiPlusNativeMethods.GdipCreatePen1(0xFFFFFFFF, 1.5f, 0, out _penFlapHighlight);
        GdiPlusNativeMethods.GdipCreatePen1(0x99506070, 1.5f, 0, out _penCrease);
        GdiPlusNativeMethods.GdipCreatePen1(0xCC94A3B8, 1.0f, 0, out _penEdge);
        _gdiResourcesCreated = true;
    }

    private void DisposeGdiResources()
    {
        if (!_gdiResourcesCreated) return;
        if (_brushShadow != 0) { GdiPlusNativeMethods.GdipDeleteBrush(_brushShadow); _brushShadow = 0; }
        if (_brushRevealedBg != 0) { GdiPlusNativeMethods.GdipDeleteBrush(_brushRevealedBg); _brushRevealedBg = 0; }
        if (_brushFlap != 0) { GdiPlusNativeMethods.GdipDeleteBrush(_brushFlap); _brushFlap = 0; }
        if (_penFlapHighlight != 0) { GdiPlusNativeMethods.GdipDeletePen(_penFlapHighlight); _penFlapHighlight = 0; }
        if (_penCrease != 0) { GdiPlusNativeMethods.GdipDeletePen(_penCrease); _penCrease = 0; }
        if (_penEdge != 0) { GdiPlusNativeMethods.GdipDeletePen(_penEdge); _penEdge = 0; }
        _gdiResourcesCreated = false;
    }

    private void ClearDirtyRegion(int curLeft, int curTop, int curRight, int curBottom)
    {
        if (_pBits == 0 || _bufWidth <= 0 || _bufHeight <= 0) return;

        if (!_hasDirtyRegion)
        {
            var totalBytes = _bufWidth * _bufHeight * 4;
            NativeMethods.ZeroMemory(_pBits, (nuint)totalBytes);
            _hasDirtyRegion = true;
            _lastDirtyLeft = curLeft;
            _lastDirtyTop = curTop;
            _lastDirtyRight = curRight;
            _lastDirtyBottom = curBottom;
            return;
        }

        var clearLeft = Math.Max(0, Math.Min(_lastDirtyLeft, curLeft));
        var clearTop = Math.Max(0, Math.Min(_lastDirtyTop, curTop));
        var clearRight = Math.Min(_bufWidth, Math.Max(_lastDirtyRight, curRight));
        var clearBottom = Math.Min(_bufHeight, Math.Max(_lastDirtyBottom, curBottom));

        if (clearRight > clearLeft && clearBottom > clearTop)
        {
            var pitch = _bufWidth * 4;
            var rowBytes = (nuint)((clearRight - clearLeft) * 4);
            for (var y = clearTop; y < clearBottom; y++)
            {
                var rowPtr = _pBits + (y * pitch) + (clearLeft * 4);
                NativeMethods.ZeroMemory(rowPtr, rowBytes);
            }
        }

        _lastDirtyLeft = curLeft;
        _lastDirtyTop = curTop;
        _lastDirtyRight = curRight;
        _lastDirtyBottom = curBottom;
    }

    private void RenderHoverSurface(int width, int height)
    {
        if (_hdcMem == 0 || _pBits == 0) return;

        EnsureGdiResources();

        var right = (float)width;
        var top = 0.0f;
        var peelSize = 36.0f;

        var curLeft = (int)Math.Max(0, right - peelSize - 10);
        var curTop = 0;
        var curRight = width;
        var curBottom = (int)Math.Min(height, top + peelSize + 10);
        ClearDirtyRegion(curLeft, curTop, curRight, curBottom);

        GdiPlusNativeMethods.GdipCreateFromHDC(_hdcMem, out var g);
        if (g != 0)
        {
            GdiPlusNativeMethods.GdipSetSmoothingMode(g, 2); // AntiAlias

            // 1. Shadow underneath the peeled corner
            var shadowPts = new GdiPlusNativeMethods.GpPointF[]
            {
                new() { X = right - peelSize - 4, Y = top },
                new() { X = right, Y = top },
                new() { X = right, Y = top + peelSize + 4 }
            };
            GdiPlusNativeMethods.GdipFillPolygon(g, _brushShadow, shadowPts, 3, 0);

            // 2. Revealed underlying darker surface
            var revealedPts = new GdiPlusNativeMethods.GpPointF[]
            {
                new() { X = right - peelSize, Y = top },
                new() { X = right, Y = top },
                new() { X = right, Y = top + peelSize }
            };
            GdiPlusNativeMethods.GdipFillPolygon(g, _brushRevealedBg, revealedPts, 3, 0);

            // 3. Folded corner paper flap
            var flapPts = new GdiPlusNativeMethods.GpPointF[]
            {
                new() { X = right - peelSize, Y = top },
                new() { X = right - (peelSize * 0.75f), Y = top + (peelSize * 0.75f) },
                new() { X = right, Y = top + peelSize }
            };
            GdiPlusNativeMethods.GdipFillPolygon(g, _brushFlap, flapPts, 3, 0);

            // 4. Subtle flap border
            GdiPlusNativeMethods.GdipDrawLine(g, _penEdge, right - peelSize, top, right - (peelSize * 0.75f), top + (peelSize * 0.75f));
            GdiPlusNativeMethods.GdipDrawLine(g, _penEdge, right - (peelSize * 0.75f), top + (peelSize * 0.75f), right, top + peelSize);

            GdiPlusNativeMethods.GdipDeleteGraphics(g);
        }

        UpdateLayeredWindow(width, height);
    }

    private void RenderCurlFrame(CornerGestureContext context, int width, int height)
    {
        if (_hdcMem == 0 || _pBits == 0) return;

        EnsureGdiResources();

        var w = (float)width;
        var h = (float)height;

        var topFoldX = (float)Math.Clamp(context.TopFoldX > 0 ? context.TopFoldX : w - (context.Progress * w * 0.8), 0.0, w);
        var rightFoldY = (float)Math.Clamp(context.RightFoldY > 0 ? context.RightFoldY : context.Progress * h * 0.8, 0.0, h);
        var apexX = (float)Math.Clamp(context.CurrentX, 0.0, w);
        var apexY = (float)Math.Clamp(context.CurrentY, 0.0, h);

        var curLeft = (int)Math.Max(0, Math.Min(topFoldX - 30, apexX - 30));
        var curTop = 0;
        var curRight = width;
        var curBottom = (int)Math.Min(height, Math.Max(rightFoldY + 30, apexY + 30));
        ClearDirtyRegion(curLeft, curTop, curRight, curBottom);

        GdiPlusNativeMethods.GdipCreateFromHDC(_hdcMem, out var g);
        if (g != 0)
        {
            GdiPlusNativeMethods.GdipSetSmoothingMode(g, 2); // AntiAlias

            // 1. Draw revealed next candidate image or solid dark background
            var revealedPts = new GdiPlusNativeMethods.GpPointF[]
            {
                new() { X = topFoldX, Y = 0 },
                new() { X = w, Y = 0 },
                new() { X = w, Y = rightFoldY }
            };

            var nextImg = Volatile.Read(ref _nextGdiImage);
            if (nextImg != 0)
            {
                GdiPlusNativeMethods.GdipGetImageWidth(nextImg, out var imgW);
                GdiPlusNativeMethods.GdipGetImageHeight(nextImg, out var imgH);

                GdiPlusNativeMethods.GdipCreatePath(0, out var path);
                if (path != 0)
                {
                    GdiPlusNativeMethods.GdipAddPathPolygon(path, revealedPts, 3);
                    GdiPlusNativeMethods.GdipSetClipPath(g, path, 0); // Replace clip

                    GdiPlusNativeMethods.GdipDrawImageRectRectI(g, nextImg,
                        0, 0, (int)w, (int)h,
                        0, 0, imgW, imgH, 2, 0, 0, 0);

                    GdiPlusNativeMethods.GdipResetClip(g);
                    GdiPlusNativeMethods.GdipDeletePath(path);
                }
            }
            else
            {
                // Fallback rich dark revealed background
                GdiPlusNativeMethods.GdipFillPolygon(g, _brushRevealedBg, revealedPts, 3, 0);
            }

            // 2. Drop shadow under the fold line
            var shadowPts = new GdiPlusNativeMethods.GpPointF[]
            {
                new() { X = Math.Max(0, topFoldX - 20), Y = 0 },
                new() { X = topFoldX, Y = 0 },
                new() { X = w, Y = rightFoldY },
                new() { X = w, Y = Math.Min(h, rightFoldY + 20) }
            };
            GdiPlusNativeMethods.GdipFillPolygon(g, _brushShadow, shadowPts, 4, 0);

            // 3. Curled paper flap
            var flapPts = new GdiPlusNativeMethods.GpPointF[]
            {
                new() { X = topFoldX, Y = 0 },
                new() { X = apexX, Y = apexY },
                new() { X = w, Y = rightFoldY }
            };

            // Subtle paper wave displacement if enabled
            if (context.PaperWaveEnabled)
            {
                var waveAmp = (float)(Math.Sin(context.Progress * Math.PI * 4) * 6.0);
                flapPts[1].X += waveAmp;
                flapPts[1].Y -= waveAmp * 0.5f;
            }

            // Paper flap body (backside of page)
            GdiPlusNativeMethods.GdipFillPolygon(g, _brushFlap, flapPts, 3, 0);

            // Flap edge highlight line
            GdiPlusNativeMethods.GdipDrawLine(g, _penFlapHighlight, topFoldX, 0, apexX, apexY);
            GdiPlusNativeMethods.GdipDrawLine(g, _penFlapHighlight, apexX, apexY, w, rightFoldY);

            // Fold crease line
            GdiPlusNativeMethods.GdipDrawLine(g, _penCrease, topFoldX, 0, w, rightFoldY);

            GdiPlusNativeMethods.GdipDeleteGraphics(g);
        }

        UpdateLayeredWindow(width, height);
    }

    private void UpdateLayeredWindow(int width, int height)
    {
        var ptDst = new NativeMethods.POINT { X = 0, Y = 0 };
        var size = new NativeMethods.SIZE { cx = width, cy = height };
        var ptSrc = new NativeMethods.POINT { X = 0, Y = 0 };

        var blend = new NativeMethods.BLENDFUNCTION
        {
            BlendOp = AcSrcOver,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = AcSrcAlpha
        };

        var hdcScreen = NativeMethods.GetDC(0);
        try
        {
            NativeMethods.UpdateLayeredWindow(
                _hWnd,
                hdcScreen,
                ref ptDst,
                ref size,
                _hdcMem,
                ref ptSrc,
                0,
                ref blend,
                UlwAlpha);

            _renderedFrameCount++;
        }
        finally
        {
            if (hdcScreen != 0)
            {
                NativeMethods.ReleaseDC(0, hdcScreen);
            }
        }
    }

    private void EnsureBuffer(int width, int height)
    {
        if (_bufWidth >= width && _bufHeight >= height && _hdcMem != 0)
        {
            return;
        }

        DisposeBuffer();

        var hdcScreen = NativeMethods.GetDC(0);
        _hdcMem = NativeMethods.CreateCompatibleDC(hdcScreen);

        var bmi = new NativeMethods.BITMAPINFO
        {
            bmiHeader = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // Top-down DIB
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0 // BI_RGB
            }
        };

        _hBitmap = NativeMethods.CreateDIBSection(_hdcMem, in bmi, 0, out _pBits, 0, 0);
        _oldBitmap = NativeMethods.SelectObject(_hdcMem, _hBitmap);
        _bufWidth = width;
        _bufHeight = height;

        NativeMethods.ReleaseDC(0, hdcScreen);
    }

    private void EnsureLoadedImages(string? nextImagePath, string? currentImagePath)
    {
        if (nextImagePath == _loadedNextImagePath)
        {
            return;
        }

        _loadedNextImagePath = nextImagePath;

        if (string.IsNullOrWhiteSpace(nextImagePath))
        {
            var old = Interlocked.Exchange(ref _nextGdiImage, 0);
            if (old != 0) GdiPlusNativeMethods.GdipDisposeImage(old);
            return;
        }

        var ext = Path.GetExtension(nextImagePath).ToLowerInvariant();
        if (ext is ".mp4" or ".webm" or ".avi" or ".mov" or ".mkv")
        {
            // Video without poster - keep fallback GDI image as 0
            var old = Interlocked.Exchange(ref _nextGdiImage, 0);
            if (old != 0) GdiPlusNativeMethods.GdipDisposeImage(old);
            return;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                if (File.Exists(nextImagePath))
                {
                    if (GdiPlusNativeMethods.GdipLoadImageFromFile(nextImagePath, out var newImg) == 0 && newImg != 0)
                    {
                        var old = Interlocked.Exchange(ref _nextGdiImage, newImg);
                        if (old != 0)
                        {
                            GdiPlusNativeMethods.GdipDisposeImage(old);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[PageTurnVisualOverlay] Error preloading image asynchronously: {ex}");
            }
        });
    }

    private void DisposeBuffer()
    {
        if (_oldBitmap != 0 && _hdcMem != 0)
        {
            NativeMethods.SelectObject(_hdcMem, _oldBitmap);
            _oldBitmap = 0;
        }
        if (_hBitmap != 0)
        {
            NativeMethods.DeleteObject(_hBitmap);
            _hBitmap = 0;
        }
        if (_hdcMem != 0)
        {
            NativeMethods.DeleteDC(_hdcMem);
            _hdcMem = 0;
        }
        _bufWidth = 0;
        _bufHeight = 0;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Hide();

        DisposeGdiResources();

        var nextImg = Interlocked.Exchange(ref _nextGdiImage, 0);
        if (nextImg != 0)
        {
            GdiPlusNativeMethods.GdipDisposeImage(nextImg);
        }

        DisposeBuffer();

        if (_hWnd != 0)
        {
            NativeMethods.DestroyWindow(_hWnd);
            _hWnd = 0;
        }

        if (_gdiToken != 0)
        {
            GdiPlusNativeMethods.GdiplusShutdown(_gdiToken);
            _gdiToken = 0;
        }
    }

    private static class NativeMethods
    {
        public delegate nint WndProc(nint hWnd, uint uMsg, nuint wParam, nint lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE
        {
            public int cx;
            public int cy;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public uint bmiColors;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public nint lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public nint hInstance;
            public nint hIcon;
            public nint hCursor;
            public nint hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public nint hIconSm;
        }

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern nint CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

        [DllImport("user32.dll", EntryPoint = "DefWindowProcW", CharSet = CharSet.Unicode)]
        public static extern nint DefWindowProc(nint hWnd, uint uMsg, nuint wParam, nint lParam);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyWindow(nint hWnd);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(nint hWnd, int nCmdShow);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern nint GetDC(nint hWnd);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern int ReleaseDC(nint hWnd, nint hDC);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UpdateLayeredWindow(
            nint hwnd,
            nint hdcDst,
            ref POINT pptDst,
            ref SIZE psize,
            nint hdcSrc,
            ref POINT pptSrc,
            uint crKey,
            ref BLENDFUNCTION pblend,
            uint dwFlags);

        [DllImport("user32.dll", EntryPoint = "LoadCursorW", CharSet = CharSet.Unicode)]
        public static extern nint LoadCursor(nint hInstance, int lpCursorName);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern nint CreateCompatibleDC(nint hdc);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern nint CreateDIBSection(nint hdc, in BITMAPINFO pbmi, uint iUsage, out nint ppvBits, nint hSection, uint dwOffset);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        public static extern nint SelectObject(nint hdc, nint h);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteDC(nint hdc);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(nint ho);

        [DllImport("kernel32.dll", EntryPoint = "RtlZeroMemory")]
        public static extern void ZeroMemory(nint dest, nuint size);

        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
        public static extern nint GetModuleHandle(string? lpModuleName);
    }

    private static class GdiPlusNativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct GdiplusStartupInput
        {
            public uint GdiplusVersion;
            public nint DebugEventCallback;
            public int SuppressBackgroundThread;
            public int SuppressExternalCodecs;

            public static GdiplusStartupInput Default => new() { GdiplusVersion = 1 };
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct GpPointF
        {
            public float X;
            public float Y;
        }

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        public static extern int GdiplusStartup(out nint token, in GdiplusStartupInput input, out nint output);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        public static extern int GdiplusShutdown(nint token);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        public static extern int GdipCreateFromHDC(nint hdc, out nint graphics);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        public static extern int GdipDeleteGraphics(nint graphics);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        public static extern int GdipSetSmoothingMode(nint graphics, int smoothingMode);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        public static extern int GdipCreateSolidFill(uint argb, out nint brush);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        public static extern int GdipDeleteBrush(nint brush);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        public static extern int GdipFillPolygon(nint graphics, nint brush, GpPointF[] points, int count, int fillMode);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        public static extern int GdipCreatePen1(uint color, float width, int unit, out nint pen);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        public static extern int GdipDeletePen(nint pen);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        public static extern int GdipDrawLine(nint graphics, nint pen, float x1, float y1, float x2, float y2);

        [DllImport("gdiplus.dll", EntryPoint = "GdipLoadImageFromFile", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int GdipLoadImageFromFile(string filename, out nint image);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        public static extern int GdipDisposeImage(nint image);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        public static extern int GdipGetImageWidth(nint image, out int width);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        public static extern int GdipGetImageHeight(nint image, out int height);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        public static extern int GdipCreatePath(int brushMode, out nint path);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        public static extern int GdipDeletePath(nint path);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        public static extern int GdipAddPathPolygon(nint path, GpPointF[] points, int count);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        public static extern int GdipSetClipPath(nint graphics, nint path, int combineMode);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        public static extern int GdipResetClip(nint graphics);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        public static extern int GdipDrawImageRectRectI(
            nint graphics,
            nint image,
            int dstx, int dsty, int dstwidth, int dstheight,
            int srcx, int srcy, int srcwidth, int srcheight,
            int srcUnit, nint imageAttributes, nint callback, nint callbackData);
    }
}
