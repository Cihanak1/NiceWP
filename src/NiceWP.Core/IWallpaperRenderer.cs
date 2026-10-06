namespace NiceWP.Core;

/// <summary>All calls, including disposal, run on the owning UI thread. Load once, attach, then show.</summary>
public interface IWallpaperRenderer : IDisposable
{
    nint WindowHandle { get; }
    Task<WallpaperResult> LoadAsync(string path);
    void Show();
}
