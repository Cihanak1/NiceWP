namespace NiceWP.Core;

/// <summary>All calls, including disposal, run on the owning UI thread. Load once, attach, then show.</summary>
public interface IWallpaperRenderer : IDisposable
{
    nint WindowHandle { get; }
    Task<WallpaperResult> LoadAsync(string path);
    void Show();
    void Pause() { }
    void Resume() { }
    void DeepFreeze() { Pause(); }
    void RestoreFromDeepFreeze() { Resume(); }

    bool IsDeepFrozen => false;

    uint? SourceWidth => null;
    uint? SourceHeight => null;
    uint? DecodeWidth => null;
    uint? DecodeHeight => null;
    long? ApproximateDecodedMemoryBytes => null;
    uint? VideoWidth => null;
    uint? VideoHeight => null;
    TimeSpan? PlaybackPosition => null;
    TimeSpan? PlaybackDuration => null;
    string? PlaybackStatus => null;
    bool IsWindowAlive => WindowHandle != 0;
}
