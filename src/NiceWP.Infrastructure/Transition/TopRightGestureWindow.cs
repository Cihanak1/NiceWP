using System.Diagnostics;
using System.Runtime.InteropServices;
using NiceWP.Core;
using NiceWP.Infrastructure.Library;
using NiceWP.Infrastructure.Rotation;
using NiceWP.Infrastructure.Settings;

namespace NiceWP.Infrastructure.Transition;

/// <summary>
/// A dedicated, lightweight Win32 layered input and rendering window positioned
/// at the top-right hot corner (32x32 px) of the primary display.
/// Captures mouse input upon click, tracks drag trajectory across the monitor via SetCapture,
/// renders realistic paper curl, drop shadow, and next candidate preview via UpdateLayeredWindow,
/// and commits or cancels cleanly without interfering with Windows desktop icons.
/// </summary>
public sealed class TopRightGestureWindow : IDisposable
{
    private const string WindowClassName = "NiceWP_TopRightGestureWindow";
    private const int CornerSize = 32;

    // Win32 constants
    private const uint WsPopup = 0x80000000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExTopMost = 0x00000008;
    private const uint WsExNoActivate = 0x08000000;
    private const uint WsExLayered = 0x00080000;

    private const int SwShowNoActivate = 4;
    private const int SwHide = 0;

    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoZOrder = 0x0004;
    private static readonly nint HwndTopMost = new(-1);

    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmCaptureChanged = 0x0215;
    private const uint WmKeyDown = 0x0100;
    private const uint WmMouseLeave = 0x02A3;
    private const uint WmDisplayChange = 0x007E;
    private const uint WmDestroy = 0x0002;

    private const int VkEscape = 0x1B;
    private const uint TmeLeave = 0x00000002;
    private const uint UlwAlpha = 0x00000002;
    private const byte AcSrcOver = 0x00;
    private const byte AcSrcAlpha = 0x01;

    // Core transition dependencies
    private readonly WallpaperTransitionCoordinator _coordinator;
    private readonly CornerPointerCaptureManager _captureManager;
    private readonly Func<WallpaperRotationService?> _getRotation;
    private readonly Func<string?> _getCurrentWallpaperPath;
    private readonly Action<WallpaperLibraryItem?>? _onTransitionCommitted;
    private readonly NativeMethods.WndProc _wndProcDelegate;

    private nint _hWnd;
    private nint _gdiToken;
    private int _screenWidth;
    private int _screenHeight;
    private bool _isCapturing;
    private bool _isDisposed;
    private bool _trackingMouseLeave;

    // Double-buffered DIB for layered window
    private nint _hdcMem;
    private nint _hBitmap;
    private nint _oldBitmap;
    private nint _pBits;
    private int _bufWidth;
    private int _bufHeight;

    // Preloaded image handles for GDI+
    private nint _nextGdiImage;
    private string? _loadedNextImagePath;

    public nint Handle => _hWnd;
    public bool IsCapturing => _isCapturing;
    public int ScreenWidth => _screenWidth;
    public int ScreenHeight => _screenHeight;

    public TopRightGestureWindow(
        WallpaperTransitionCoordinator coordinator,
        CornerPointerCaptureManager captureManager,
        Func<WallpaperRotationService?> getRotation,
        Func<string?> getCurrentWallpaperPath,
        Action<WallpaperLibraryItem?>? onTransitionCommitted = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _captureManager = captureManager ?? throw new ArgumentNullException(nameof(captureManager));
        _getRotation = getRotation ?? throw new ArgumentNullException(nameof(getRotation));
        _getCurrentWallpaperPath = getCurrentWallpaperPath ?? throw new ArgumentNullException(nameof(getCurrentWallpaperPath));
        _onTransitionCommitted = onTransitionCommitted;

        _wndProcDelegate = WndProc;

        InitializeScreenDimensions();
        InitializeGdiPlus();
        RegisterAndCreateWindow();
        UpdateVisibility();
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
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
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

        var x = _screenWidth - CornerSize;
        var y = 0;

        _hWnd = NativeMethods.CreateWindowEx(
            WsExToolWindow | WsExTopMost | WsExNoActivate | WsExLayered,
            WindowClassName,
            "NiceWP Hot Corner",
            WsPopup,
            x, y, CornerSize, CornerSize,
            0, 0, hInstance, 0);

        if (_hWnd != 0)
        {
            EnsureBuffer(_screenWidth, _screenHeight);
            RenderTransparentSurface(CornerSize, CornerSize);
        }
    }

    /// <summary>
    /// Updates window visibility based on whether interactive rotation is enabled.
    /// </summary>
    public void UpdateVisibility()
    {
        if (_hWnd == 0 || _isDisposed) return;

        var permitted = _coordinator.CanInitiateTransition();
        if (permitted)
        {
            NativeMethods.ShowWindow(_hWnd, SwShowNoActivate);
            NativeMethods.SetWindowPos(_hWnd, HwndTopMost, _screenWidth - CornerSize, 0, CornerSize, CornerSize, SwpNoActivate | SwpShowWindow);
        }
        else if (!_isCapturing)
        {
            NativeMethods.ShowWindow(_hWnd, SwHide);
        }
    }

    private nint WndProc(nint hWnd, uint uMsg, nuint wParam, nint lParam)
    {
        switch (uMsg)
        {
            case WmMouseMove:
                HandleMouseMove();
                return 0;

            case WmLButtonDown:
                HandleLButtonDown();
                return 0;

            case WmLButtonUp:
                HandleLButtonUp();
                return 0;

            case WmMouseLeave:
                HandleMouseLeave();
                return 0;

            case WmKeyDown:
                if ((int)wParam == VkEscape && _isCapturing)
                {
                    HandleCancelGesture("EscapeKey");
                }
                return 0;

            case WmCaptureChanged:
                if (_isCapturing && lParam != hWnd)
                {
                    HandleCancelGesture("CaptureLost");
                }
                return 0;

            case WmDisplayChange:
                InitializeScreenDimensions();
                EnsureBuffer(_screenWidth, _screenHeight);
                if (!_isCapturing)
                {
                    NativeMethods.SetWindowPos(_hWnd, HwndTopMost, _screenWidth - CornerSize, 0, CornerSize, CornerSize, SwpNoActivate);
                }
                return 0;

            case WmDestroy:
                return 0;
        }

        return NativeMethods.DefWindowProc(hWnd, uMsg, wParam, lParam);
    }

    private void HandleMouseMove()
    {
        if (_isDisposed) return;

        if (!_isCapturing)
        {
            if (!_trackingMouseLeave)
            {
                var tme = new NativeMethods.TRACKMOUSEEVENT
                {
                    cbSize = (uint)Marshal.SizeOf<NativeMethods.TRACKMOUSEEVENT>(),
                    dwFlags = TmeLeave,
                    hwndTrack = _hWnd,
                    dwHoverTime = 0
                };
                NativeMethods.TrackMouseEvent(ref tme);
                _trackingMouseLeave = true;
            }

            if (_coordinator.CanInitiateTransition())
            {
                _coordinator.SetHoverPreview(true);
                RenderHoverPreview();
            }
        }
        else
        {
            NativeMethods.GetCursorPos(out var pt);
            _captureManager.ProcessPointerMoved(1, pt.X, pt.Y);
            _coordinator.UpdateGesture(pt.X, pt.Y);

            if (_coordinator.CurrentContext != null)
            {
                RenderGestureFrame(_coordinator.CurrentContext);
            }
        }
    }

    private void HandleMouseLeave()
    {
        _trackingMouseLeave = false;
        if (!_isCapturing && !_isDisposed)
        {
            _coordinator.SetHoverPreview(false);
            RenderTransparentSurface(CornerSize, CornerSize);
        }
    }

    private void HandleLButtonDown()
    {
        if (_isDisposed) return;

        if (!_coordinator.CanInitiateTransition())
        {
            return;
        }

        NativeMethods.GetCursorPos(out var pt);
        NativeMethods.SetCapture(_hWnd);
        _isCapturing = true;

        // Expand window to fullscreen overlay to track across monitor
        NativeMethods.SetWindowPos(_hWnd, HwndTopMost, 0, 0, _screenWidth, _screenHeight, SwpNoActivate | SwpShowWindow);

        _ = _captureManager.TryAcquireCaptureAsync(1, pt.X, pt.Y);

        // Preload next candidate image
        var nextCandidate = _coordinator.PeekedCandidate ?? _getRotation()?.PeekNextCandidate();
        LoadCandidateImage(nextCandidate?.FilePath);

        if (_coordinator.CurrentContext != null)
        {
            RenderGestureFrame(_coordinator.CurrentContext);
        }
    }

    private void HandleLButtonUp()
    {
        if (!_isCapturing || _isDisposed) return;

        _isCapturing = false;
        NativeMethods.ReleaseCapture();

        NativeMethods.GetCursorPos(out var pt);
        var progress = _coordinator.CurrentContext?.Progress ?? 0.0;

        if (progress >= 0.30)
        {
            HandleCommitGesture();
        }
        else
        {
            HandleCancelGesture("ReleaseUnderThreshold");
        }
    }

    private void HandleCommitGesture()
    {
        var candidate = _coordinator.PeekedCandidate;

        // Animate completion
        AnimateProgressTo(1.0, onComplete: () =>
        {
            _ = _coordinator.CommitAsync(animate: false);
            _onTransitionCommitted?.Invoke(candidate);

            // Shrink back to hot corner
            ResetToHotCorner();
        });
    }

    private void HandleCancelGesture(string reason)
    {
        // Animate spring back to rest
        AnimateProgressTo(0.0, onComplete: () =>
        {
            _ = _coordinator.CancelAsync(animate: false);
            ResetToHotCorner();
        });
    }

    private void ResetToHotCorner()
    {
        if (_hWnd == 0 || _isDisposed) return;

        NativeMethods.SetWindowPos(
            _hWnd,
            HwndTopMost,
            _screenWidth - CornerSize,
            0,
            CornerSize,
            CornerSize,
            SwpNoActivate);

        RenderTransparentSurface(CornerSize, CornerSize);
        UpdateVisibility();
    }

    private void AnimateProgressTo(double targetProgress, Action onComplete)
    {
        var startProgress = _coordinator.CurrentContext?.Progress ?? 0.0;
        var startX = _coordinator.CurrentContext?.CurrentX ?? _screenWidth;
        var startY = _coordinator.CurrentContext?.CurrentY ?? 0;

        var targetX = targetProgress >= 0.99 ? _screenWidth - 800 : _screenWidth;
        var targetY = targetProgress >= 0.99 ? 800 : 0;

        _ = Task.Run(async () =>
        {
            const int frames = 8;
            const int delayMs = 15;

            for (var i = 1; i <= frames; i++)
            {
                var t = (double)i / frames;
                var eased = 1.0 - Math.Pow(1.0 - t, 2); // Ease-out quadratic

                var curX = startX + ((targetX - startX) * eased);
                var curY = startY + ((targetY - startY) * eased);

                _coordinator.UpdateGesture(curX, curY);
                if (_coordinator.CurrentContext != null)
                {
                    RenderGestureFrame(_coordinator.CurrentContext);
                }

                await Task.Delay(delayMs).ConfigureAwait(false);
            }

            onComplete();
        });
    }

    private void LoadCandidateImage(string? path)
    {
        if (string.Equals(_loadedNextImagePath, path, StringComparison.OrdinalIgnoreCase) && _nextGdiImage != 0)
        {
            return;
        }

        if (_nextGdiImage != 0)
        {
            GdiPlusNativeMethods.GdipDisposeImage(_nextGdiImage);
            _nextGdiImage = 0;
            _loadedNextImagePath = null;
        }

        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            var ext = Path.GetExtension(path);
            if (string.Equals(ext, ".mp4", StringComparison.OrdinalIgnoreCase))
            {
                // Video: check for thumbnail poster frame
                var thumb = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, Path.GetFileNameWithoutExtension(path) + ".png");
                if (File.Exists(thumb))
                {
                    path = thumb;
                }
            }

            var status = GdiPlusNativeMethods.GdipLoadImageFromFile(path, out var img);
            if (status == 0 && img != 0)
            {
                _nextGdiImage = img;
                _loadedNextImagePath = path;
            }
        }
    }

    private void EnsureBuffer(int width, int height)
    {
        if (_bufWidth == width && _bufHeight == height && _hdcMem != 0)
        {
            return;
        }

        DestroyBuffer();

        var hdcScreen = NativeMethods.GetDC(0);
        _hdcMem = NativeMethods.CreateCompatibleDC(hdcScreen);

        var bmi = new NativeMethods.BITMAPINFO
        {
            bmiHeader = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // Top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0 // BI_RGB
            }
        };

        _hBitmap = NativeMethods.CreateDIBSection(_hdcMem, in bmi, 0, out _pBits, 0, 0);
        _oldBitmap = NativeMethods.SelectObject(_hdcMem, _hBitmap);
        NativeMethods.ReleaseDC(0, hdcScreen);

        _bufWidth = width;
        _bufHeight = height;
    }

    private void DestroyBuffer()
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

    private void RenderTransparentSurface(int width, int height)
    {
        if (_hWnd == 0 || _hdcMem == 0 || _pBits == 0) return;

        // Clear buffer memory to 0 (fully transparent)
        var totalBytes = _bufWidth * _bufHeight * 4;
        NativeMethods.ZeroMemory(_pBits, (nuint)totalBytes);

        UpdateLayeredWindow(width, height);
    }

    private void RenderHoverPreview()
    {
        if (_hWnd == 0 || _hdcMem == 0 || _isDisposed) return;

        EnsureBuffer(_bufWidth > 0 ? _bufWidth : CornerSize, _bufHeight > 0 ? _bufHeight : CornerSize);

        var totalBytes = _bufWidth * _bufHeight * 4;
        NativeMethods.ZeroMemory(_pBits, (nuint)totalBytes);

        // Draw small 16px corner peel preview in GDI+
        GdiPlusNativeMethods.GdipCreateFromHDC(_hdcMem, out var g);
        if (g != 0)
        {
            GdiPlusNativeMethods.GdipSetSmoothingMode(g, 2); // AntiAlias

            // Revealed dark corner triangle
            var ptsRevealed = new GdiPlusNativeMethods.GpPointF[]
            {
                new() { X = 16, Y = 0 },
                new() { X = 32, Y = 0 },
                new() { X = 32, Y = 16 }
            };
            GdiPlusNativeMethods.GdipCreateSolidFill(0xDD202630, out var brushRevealed);
            GdiPlusNativeMethods.GdipFillPolygon(g, brushRevealed, ptsRevealed, 3, 0);
            GdiPlusNativeMethods.GdipDeleteBrush(brushRevealed);

            // Small shadow line
            GdiPlusNativeMethods.GdipCreatePen1(0x990F141C, 1.5f, 0, out var penShadow);
            GdiPlusNativeMethods.GdipDrawLine(g, penShadow, 16, 0, 32, 16);
            GdiPlusNativeMethods.GdipDeletePen(penShadow);

            // Lifted paper flap triangle
            var ptsFlap = new GdiPlusNativeMethods.GpPointF[]
            {
                new() { X = 16, Y = 0 },
                new() { X = 20, Y = 12 },
                new() { X = 32, Y = 16 }
            };
            GdiPlusNativeMethods.GdipCreateSolidFill(0xFFF0F4F8, out var brushFlap);
            GdiPlusNativeMethods.GdipFillPolygon(g, brushFlap, ptsFlap, 3, 0);
            GdiPlusNativeMethods.GdipDeleteBrush(brushFlap);

            GdiPlusNativeMethods.GdipCreatePen1(0xAA94A3B8, 1.0f, 0, out var penEdge);
            GdiPlusNativeMethods.GdipDrawLine(g, penEdge, 16, 0, 20, 12);
            GdiPlusNativeMethods.GdipDrawLine(g, penEdge, 20, 12, 32, 16);
            GdiPlusNativeMethods.GdipDeletePen(penEdge);

            GdiPlusNativeMethods.GdipDeleteGraphics(g);
        }

        UpdateLayeredWindow(CornerSize, CornerSize);
    }

    private void RenderGestureFrame(CornerGestureContext context)
    {
        if (_hWnd == 0 || _hdcMem == 0 || _isDisposed) return;

        var w = _screenWidth;
        var h = _screenHeight;

        EnsureBuffer(w, h);

        var totalBytes = _bufWidth * _bufHeight * 4;
        NativeMethods.ZeroMemory(_pBits, (nuint)totalBytes);

        var topFoldX = (float)Math.Clamp(context.TopFoldX > 0 ? context.TopFoldX : w - (context.Progress * 300), 0.0, w);
        var rightFoldY = (float)Math.Clamp(context.RightFoldY > 0 ? context.RightFoldY : context.Progress * 300, 0.0, h);
        var apexX = (float)Math.Clamp(context.CurrentX, 0.0, w);
        var apexY = (float)Math.Clamp(context.CurrentY, 0.0, h);

        GdiPlusNativeMethods.GdipCreateFromHDC(_hdcMem, out var g);
        if (g != 0)
        {
            GdiPlusNativeMethods.GdipSetSmoothingMode(g, 2); // AntiAlias

            // 1. Draw revealed next candidate wallpaper underneath the peeled corner
            if (_nextGdiImage != 0)
            {
                // Clip next image to the revealed triangle (topFoldX, 0) -> (w, 0) -> (w, rightFoldY)
                var revealedPts = new GdiPlusNativeMethods.GpPointF[]
                {
                    new() { X = topFoldX, Y = 0 },
                    new() { X = w, Y = 0 },
                    new() { X = w, Y = rightFoldY }
                };

                GdiPlusNativeMethods.GdipCreateSolidFill(0xFF1E293B, out var bgBrush);
                GdiPlusNativeMethods.GdipFillPolygon(g, bgBrush, revealedPts, 3, 0);
                GdiPlusNativeMethods.GdipDeleteBrush(bgBrush);

                // Draw next candidate image
                GdiPlusNativeMethods.GdipDrawImageRect(g, _nextGdiImage, 0, 0, w, h);
            }
            else
            {
                // Fallback elegant slate preview surface if image is loading
                var revealedPts = new GdiPlusNativeMethods.GpPointF[]
                {
                    new() { X = topFoldX, Y = 0 },
                    new() { X = w, Y = 0 },
                    new() { X = w, Y = rightFoldY }
                };
                GdiPlusNativeMethods.GdipCreateSolidFill(0xEE1E293B, out var fill);
                GdiPlusNativeMethods.GdipFillPolygon(g, fill, revealedPts, 3, 0);
                GdiPlusNativeMethods.GdipDeleteBrush(fill);
            }

            // 2. Draw soft drop shadow along fold line
            var shadowOpacity = (float)context.ComputeShadowOpacity();
            var shadowAlpha = (byte)Math.Clamp(shadowOpacity * 255f, 0, 255);
            var shadowColor = (uint)((shadowAlpha << 24) | 0x0F1218);

            GdiPlusNativeMethods.GdipCreatePen1(shadowColor, (float)Math.Clamp(context.Progress * 12.0, 3.0, 16.0), 0, out var shadowPen);
            GdiPlusNativeMethods.GdipDrawLine(g, shadowPen, topFoldX, 0, w, rightFoldY);
            GdiPlusNativeMethods.GdipDeletePen(shadowPen);

            // 3. Draw curling paper flap
            var wave = (float)context.ComputeWaveOffset();
            var midX1 = ((topFoldX + apexX) / 2f) + wave;
            var midY1 = (apexY / 2f) + wave;
            var midX2 = ((apexX + w) / 2f) - (wave * 0.5f);
            var midY2 = ((apexY + rightFoldY) / 2f) + (wave * 0.5f);

            var flapPts = new GdiPlusNativeMethods.GpPointF[]
            {
                new() { X = topFoldX, Y = 0 },
                new() { X = midX1, Y = midY1 },
                new() { X = apexX, Y = apexY },
                new() { X = midX2, Y = midY2 },
                new() { X = w, Y = rightFoldY }
            };

            // Flap paper body
            GdiPlusNativeMethods.GdipCreateSolidFill(0xFFF1F5F9, out var flapBrush);
            GdiPlusNativeMethods.GdipFillPolygon(g, flapBrush, flapPts, 5, 0);
            GdiPlusNativeMethods.GdipDeleteBrush(flapBrush);

            // Paper edge stroke
            GdiPlusNativeMethods.GdipCreatePen1(0xCC94A3B8, 1.2f, 0, out var flapPen);
            GdiPlusNativeMethods.GdipDrawLine(g, flapPen, topFoldX, 0, apexX, apexY);
            GdiPlusNativeMethods.GdipDrawLine(g, flapPen, apexX, apexY, w, rightFoldY);
            GdiPlusNativeMethods.GdipDeletePen(flapPen);

            GdiPlusNativeMethods.GdipDeleteGraphics(g);
        }

        UpdateLayeredWindow(w, h);
    }

    private void UpdateLayeredWindow(int width, int height)
    {
        if (_hWnd == 0 || _hdcMem == 0) return;

        var hdcScreen = NativeMethods.GetDC(0);
        var ptDst = new NativeMethods.POINT { X = _isCapturing ? 0 : _screenWidth - width, Y = 0 };
        var size = new NativeMethods.SIZE { cx = width, cy = height };
        var ptSrc = new NativeMethods.POINT { X = 0, Y = 0 };
        var blend = new NativeMethods.BLENDFUNCTION
        {
            BlendOp = AcSrcOver,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = AcSrcAlpha
        };

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

        NativeMethods.ReleaseDC(0, hdcScreen);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_isCapturing && _hWnd != 0)
        {
            NativeMethods.ReleaseCapture();
            _isCapturing = false;
        }

        if (_nextGdiImage != 0)
        {
            GdiPlusNativeMethods.GdipDisposeImage(_nextGdiImage);
            _nextGdiImage = 0;
        }

        DestroyBuffer();

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

    #region Win32 & GDI P/Invoke Interop

    private static class NativeMethods
    {
        internal delegate nint WndProc(nint hWnd, uint uMsg, nuint wParam, nint lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WNDCLASSEX
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

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SIZE
        {
            public int cx;
            public int cy;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BITMAPINFOHEADER
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
        internal struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public uint bmiColors;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct TRACKMOUSEEVENT
        {
            public uint cbSize;
            public uint dwFlags;
            public nint hwndTrack;
            public uint dwHoverTime;
        }

        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

        [DllImport("user32.dll", EntryPoint = "DefWindowProcW", CharSet = CharSet.Unicode)]
        internal static extern nint DefWindowProc(nint hWnd, uint uMsg, nuint wParam, nint lParam);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyWindow(nint hWnd);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(nint hWnd, int nCmdShow);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern nint SetCapture(nint hWnd);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReleaseCapture();

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern nint GetDC(nint hWnd);

        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern int ReleaseDC(nint hWnd, nint hDC);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateLayeredWindow(
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
        internal static extern nint LoadCursor(nint hInstance, int lpCursorName);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        internal static extern nint CreateCompatibleDC(nint hdc);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        internal static extern nint CreateDIBSection(nint hdc, in BITMAPINFO pbmi, uint iUsage, out nint ppvBits, nint hSection, uint dwOffset);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        internal static extern nint SelectObject(nint hdc, nint h);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteDC(nint hdc);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteObject(nint ho);

        [DllImport("kernel32.dll", EntryPoint = "RtlZeroMemory")]
        internal static extern void ZeroMemory(nint dest, nuint size);

        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
        internal static extern nint GetModuleHandle(string? lpModuleName);
    }

    private static class GdiPlusNativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct GdiplusStartupInput
        {
            public uint GdiplusVersion;
            public nint DebugEventCallback;
            public int SuppressBackgroundThread;
            public int SuppressExternalCodecs;

            public static GdiplusStartupInput Default => new() { GdiplusVersion = 1 };
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct GpPointF
        {
            public float X;
            public float Y;
        }

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        internal static extern int GdiplusStartup(out nint token, in GdiplusStartupInput input, out nint output);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        internal static extern int GdiplusShutdown(nint token);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        internal static extern int GdipCreateFromHDC(nint hdc, out nint graphics);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        internal static extern int GdipDeleteGraphics(nint graphics);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        internal static extern int GdipSetSmoothingMode(nint graphics, int smoothingMode);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        internal static extern int GdipCreateSolidFill(uint argb, out nint brush);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        internal static extern int GdipDeleteBrush(nint brush);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        internal static extern int GdipFillPolygon(nint graphics, nint brush, GpPointF[] points, int count, int fillMode);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        internal static extern int GdipCreatePen1(uint argb, float width, int unit, out nint pen);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        internal static extern int GdipDeletePen(nint pen);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        internal static extern int GdipDrawLine(nint graphics, nint pen, float x1, float y1, float x2, float y2);

        [DllImport("gdiplus.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        internal static extern int GdipLoadImageFromFile(string filename, out nint image);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        internal static extern int GdipDisposeImage(nint image);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        internal static extern int GdipDrawImageRect(nint graphics, nint image, float x, float y, float width, float height);
    }

    #endregion
}
