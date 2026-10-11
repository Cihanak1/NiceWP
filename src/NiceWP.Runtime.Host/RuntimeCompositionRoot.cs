using System.Diagnostics;
using NiceWP.Core;
using NiceWP.Infrastructure.Settings;
using NiceWP.Platform.Windows;
using NiceWP.Rendering;

namespace NiceWP.Runtime.Host;

/// <summary>
/// Composition root for the runtime host process.
/// Assembles runtime services and renderer factories ready for execution without launching wallpaper.
/// </summary>
public sealed class RuntimeCompositionRoot : IDisposable
{
    public IDesktopHost DesktopHost { get; }
    public ImageWallpaperService WallpaperService { get; }
    public SettingsService SettingsService { get; }
    public IFullscreenWindowDetector FullscreenDetector { get; }
    public bool IsDisposed => disposed;
    private bool disposed;

    public RuntimeCompositionRoot(
        IDesktopHost? desktopHost = null,
        Func<IWallpaperRenderer>? imageRendererFactory = null,
        Func<IWallpaperRenderer>? videoRendererFactory = null,
        SettingsService? settingsService = null,
        IFullscreenWindowDetector? fullscreenDetector = null)
    {
        DesktopHost = desktopHost ?? new WindowsDesktopHost();
        var createImg = imageRendererFactory ?? (() => new ImageWallpaperRenderer());
        var createVid = videoRendererFactory ?? (() => new VideoWallpaperRenderer());
        WallpaperService = new ImageWallpaperService(DesktopHost, createImg, createVid);
        SettingsService = settingsService ?? new SettingsService();
        FullscreenDetector = fullscreenDetector ?? (desktopHost is null or WindowsDesktopHost
            ? new FullscreenWindowDetector()
            : new InactiveFullscreenWindowDetector());
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            WallpaperService.Stop();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error during wallpaper service cleanup: {ex}");
        }
    }

    private sealed class InactiveFullscreenWindowDetector : IFullscreenWindowDetector
    {
        public bool IsFullscreenWindowActive() => false;
    }
}
