using System.Diagnostics;
using NiceWP.Core;

namespace NiceWP.Runtime;

/// <summary>Owned by the UI thread. Stop also invalidates any pending start.</summary>
public sealed class ImageWallpaperService(IDesktopHost host, Func<IWallpaperRenderer> createRenderer)
{
    private readonly DesktopHostService desktop = new(host);
    private IWallpaperRenderer? active;
    private IWallpaperRenderer? rejected;
    private bool starting;
    private long generation;

    public string? ActivePath { get; private set; }

    public async Task<WallpaperResult> StartAsync(string? path)
    {
        if (starting) return new(WallpaperError.Busy, "An image is already loading.");
        if (rejected != null)
        {
            var cleanup = Stop();
            if (!cleanup.Success) return cleanup;
        }
        var validation = ImageWallpaper.Validate(path, out var fullPath);
        if (!validation.Success) return validation;
        var health = ValidateHost();
        if (!health.Success) return health;
        if (active != null && desktop.Handle != 0 && string.Equals(ActivePath, fullPath, StringComparison.OrdinalIgnoreCase)) return new();

        starting = true;
        var request = generation;
        IWallpaperRenderer? candidate = null;
        async Task<WallpaperResult> StartCoreAsync()
        {
            try
            {
                candidate = createRenderer();
                var loaded = await candidate.LoadAsync(fullPath);
                if (!loaded.Success) return loaded;
                if (request != generation) return new(WallpaperError.Stopped, "Wallpaper loading was stopped.");
                var stopped = Stop();
                if (!stopped.Success) return stopped;
                request = generation;
                active = candidate;
                candidate = null;
                var attached = await desktop.AttachAsync(active.WindowHandle);
                if (request != generation) return new(WallpaperError.Stopped, "Wallpaper loading was stopped.");
                if (!attached.Success)
                {
                    var cleanup = Stop();
                    return cleanup.Success
                        ? new(WallpaperError.DesktopHostUnavailable, $"Desktop host unavailable: {attached.Error}.") : cleanup;
                }
                active!.Show();
                ActivePath = fullPath;
                Trace.WriteLine($"Wallpaper started: {fullPath}");
                return new();
            }
            catch (Exception exception)
            {
                Trace.WriteLine(exception);
                var cleanup = Stop();
                return cleanup.Success
                    ? new(WallpaperError.RendererFailed, "The wallpaper could not be started.") : cleanup;
            }
        }

        try
        {
            var result = await StartCoreAsync();
            if (candidate != null)
            {
                rejected = candidate;
                try
                {
                    rejected.Dispose();
                    rejected = null;
                }
                catch (Exception exception)
                {
                    Trace.WriteLine(exception);
                    return new(WallpaperError.CleanupFailed, "The unused renderer could not be released. Please try closing again.");
                }
            }
            return result;
        }
        finally { starting = false; }
    }

    public WallpaperResult ValidateHost()
    {
        if (active == null || desktop.Handle == 0) return new();
        try
        {
            if (host.IsValid(desktop.Handle)) return new();
            var cleanup = Stop();
            return cleanup.Success
                ? new(WallpaperError.DesktopHostUnavailable, "The desktop host is no longer available. Select an image again.") : cleanup;
        }
        catch (Exception exception)
        {
            Trace.WriteLine(exception);
            var cleanup = Stop();
            return cleanup.Success ? new(WallpaperError.DesktopHostUnavailable, "Desktop host validation failed.") : cleanup;
        }
    }

    public WallpaperResult Stop()
    {
        generation++;
        try
        {
            desktop.Stop();
            active?.Dispose();
            active = null;
            ActivePath = null;
            rejected?.Dispose();
            rejected = null;
            Trace.WriteLine("Wallpaper stopped; desktop detached and renderer released.");
            return new();
        }
        catch (Exception exception)
        {
            Trace.WriteLine(exception);
            return new(WallpaperError.CleanupFailed, "Wallpaper cleanup failed. Please try closing again.");
        }
    }
}
