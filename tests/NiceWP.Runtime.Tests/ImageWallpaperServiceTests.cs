using NiceWP.Core;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class ImageWallpaperServiceTests : IDisposable
{
    private readonly string first = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
    private readonly string second = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jpg");
    private readonly string video = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mp4");
    private readonly List<string> events = [];
    private readonly List<FakeRenderer> renderers = [];
    private readonly FakeHost host;
    private readonly ImageWallpaperService service;
    private WallpaperResult loadResult = new();
    private TaskCompletionSource<WallpaperResult>? pending;

    public ImageWallpaperServiceTests()
    {
        File.WriteAllBytes(first, []);
        File.WriteAllBytes(second, []);
        File.WriteAllBytes(video, []);
        host = new(events);
        service = new(host, () =>
        {
            var renderer = new FakeRenderer(events, loadResult, pending);
            renderers.Add(renderer);
            return renderer;
        }, () =>
        {
            var renderer = new FakeRenderer(events, loadResult, pending, completePendingOnDispose: true);
            renderers.Add(renderer);
            return renderer;
        });
    }

    [Fact]
    public async Task StartReuseReplaceAndStopReleaseInOrder()
    {
        Assert.True((await service.StartAsync(first)).Success);
        Assert.True((await service.StartAsync(first)).Success);
        Assert.Single(renderers);
        Assert.True((await service.StartAsync(second)).Success);
        Assert.Equal(1, renderers[0].DisposeCount);
        Assert.Equal(new[] { "load", "attach", "show", "load", "detach", "dispose", "attach", "show" }, events);
        Assert.Equal(second, service.ActivePath);
        Assert.True(service.Stop().Success);
        Assert.True(service.Stop().Success);
        Assert.Equal(1, renderers[1].DisposeCount);
        Assert.Null(service.ActivePath);
    }

    [Fact]
    public async Task InvalidImagePreservesCurrentAndDisposesCandidate()
    {
        await service.StartAsync(first);
        loadResult = new(WallpaperError.InvalidImage, "Corrupt image");
        Assert.Equal(WallpaperError.InvalidImage, (await service.StartAsync(second)).Error);
        Assert.Equal(first, service.ActivePath);
        Assert.Equal(0, renderers[0].DisposeCount);
        Assert.Equal(1, renderers[1].DisposeCount);
    }

    [Fact]
    public async Task StopDuringLoadDisposesCandidateWithoutAttachment()
    {
        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = service.StartAsync(first);
        Assert.Equal(WallpaperError.Busy, (await service.StartAsync(second)).Error);
        service.Stop();
        pending.SetResult(new());
        Assert.Equal(WallpaperError.Stopped, (await start).Error);
        Assert.Equal(new[] { "load", "dispose" }, events);
        Assert.Null(service.ActivePath);
    }

    [Fact]
    public async Task LostHostStopsWallpaperAndReturnsControlledFailure()
    {
        await service.StartAsync(first);
        host.Valid = false;
        Assert.Equal(WallpaperError.DesktopHostUnavailable, service.ValidateHost().Error);
        Assert.Equal(1, renderers[0].DisposeCount);
        Assert.Null(service.ActivePath);
    }

    [Fact]
    public async Task StaleHostHandleDuringActiveWallpaper_NextStartReResolvesAndAttachesNewHandle()
    {
        Assert.True((await service.StartAsync(first)).Success);
        Assert.Equal(1, host.ResolveCount);
        Assert.Equal(10, host.LastAttachedHost);

        // Simulate Explorer restart: handle 10 becomes invalid, new WorkerW handle is 20
        host.InvalidHandles.Add(10);
        host.NextHandle = 20;

        var restartResult = await service.StartAsync(second);
        Assert.True(restartResult.Success);
        Assert.Equal(second, service.ActivePath);
        Assert.Equal(2, host.ResolveCount);
        Assert.Equal(20, host.LastAttachedHost);
        Assert.Equal(1, renderers[0].DisposeCount);
        Assert.Equal(0, renderers[1].DisposeCount);
    }

    [Fact]
    public async Task FailedAttachmentCleansRenderer()
    {
        host.FailAttach = true;
        Assert.Equal(WallpaperError.RendererFailed, (await service.StartAsync(first)).Error);
        Assert.Equal(1, renderers[0].DisposeCount);
        Assert.Null(service.ActivePath);
    }

    [Fact]
    public async Task FailedDetachCanBeRetriedBeforeRendererIsDestroyed()
    {
        await service.StartAsync(first);
        host.FailDetach = true;
        Assert.Equal(WallpaperError.CleanupFailed, service.Stop().Error);
        Assert.Equal(0, renderers[0].DisposeCount);
        host.FailDetach = false;
        Assert.True(service.Stop().Success);
        Assert.Equal(1, renderers[0].DisposeCount);
    }

    [Fact]
    public async Task FailedCandidateDisposalReturnsErrorAndCanBeRetried()
    {
        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = service.StartAsync(first);
        renderers[0].FailDispose = true;
        pending.SetResult(new(WallpaperError.InvalidImage));
        Assert.Equal(WallpaperError.CleanupFailed, (await start).Error);
        renderers[0].FailDispose = false;
        Assert.True(service.Stop().Success);
        Assert.Equal(1, renderers[0].DisposeCount);
    }

    [Fact]
    public async Task ImageVideoImageUsesOneAttachmentAndReleasesPreviousRenderer()
    {
        Assert.True((await service.StartAsync(first)).Success);
        Assert.True((await service.StartAsync(video)).Success);
        Assert.Equal(video, service.ActivePath);
        Assert.True((await service.StartAsync(second)).Success);
        Assert.Equal(new[] { "load", "attach", "show", "load", "detach", "dispose", "attach", "show",
            "load", "detach", "dispose", "attach", "show" }, events);
        Assert.Equal(1, renderers[0].DisposeCount);
        Assert.Equal(1, renderers[1].DisposeCount);
    }

    [Fact]
    public async Task FailedVideoOpenPreservesCurrentWallpaper()
    {
        await service.StartAsync(first);
        loadResult = new(WallpaperError.RendererFailed, "MediaFailed");
        Assert.Equal(WallpaperError.RendererFailed, (await service.StartAsync(video)).Error);
        Assert.Equal(first, service.ActivePath);
        Assert.Equal(0, renderers[0].DisposeCount);
        Assert.Equal(1, renderers[1].DisposeCount);
    }

    [Fact]
    public async Task StopDuringVideoOpenDisposesPendingRendererAndUnblocksStart()
    {
        await service.StartAsync(first);
        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = service.StartAsync(video);
        Assert.Equal(first, service.ActivePath);
        Assert.True(service.Stop().Success);
        Assert.Equal(WallpaperError.Stopped, (await start.WaitAsync(TimeSpan.FromSeconds(5))).Error);
        Assert.Equal(1, renderers[1].DisposeCount);
        Assert.Null(service.ActivePath);
        Assert.Equal(1, events.Count(value => value == "attach"));
    }

    [Fact]
    public async Task VideoToVideoReleasesPreviousVideoAndAttachesNew()
    {
        var secondVideo = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mp4");
        try
        {
            File.WriteAllBytes(secondVideo, []);
            Assert.True((await service.StartAsync(video)).Success);
            Assert.Equal(video, service.ActivePath);
            Assert.True((await service.StartAsync(secondVideo)).Success);
            Assert.Equal(secondVideo, service.ActivePath);
            Assert.Equal(new[] { "load", "attach", "show", "load", "detach", "dispose", "attach", "show" }, events);
            Assert.Equal(1, renderers[0].DisposeCount);
            Assert.Equal(0, renderers[1].DisposeCount);
            Assert.True(service.Stop().Success);
            Assert.Equal(1, renderers[1].DisposeCount);
        }
        finally { File.Delete(secondVideo); }
    }

    public void Dispose()
    {
        host.FailDetach = false;
        service.Stop();
        File.Delete(first);
        File.Delete(second);
        File.Delete(video);
    }

    private sealed class FakeRenderer(List<string> events, WallpaperResult result,
        TaskCompletionSource<WallpaperResult>? pending, bool completePendingOnDispose = false) : IWallpaperRenderer
    {
        public nint WindowHandle => 42;
        public int DisposeCount { get; private set; }
        public bool FailDispose { get; set; }
        public Task<WallpaperResult> LoadAsync(string path)
        {
            events.Add("load");
            return pending?.Task ?? Task.FromResult(result);
        }
        public void Show() => events.Add("show");
        public void Dispose()
        {
            if (DisposeCount != 0) return;
            if (FailDispose) throw new InvalidOperationException("Dispose failed");
            DisposeCount++;
            events.Add("dispose");
            if (completePendingOnDispose) pending?.TrySetResult(new(WallpaperError.Stopped));
        }
    }

    private sealed class FakeHost(List<string> events) : IDesktopHost
    {
        public bool Valid { get; set; } = true;
        public bool FailAttach { get; set; }
        public bool FailDetach { get; set; }
        public nint NextHandle { get; set; } = 10;
        public int ResolveCount { get; private set; }
        public nint LastAttachedHost { get; private set; }
        public HashSet<nint> InvalidHandles { get; } = [];
        public Task<DesktopHostResult> ResolveAsync(CancellationToken cancellationToken = default)
        {
            ResolveCount++;
            return Task.FromResult(new DesktopHostResult(NextHandle, DesktopHostError.None));
        }
        public bool IsValid(nint handle) => Valid && !InvalidHandles.Contains(handle);
        public nint Attach(nint host, nint window)
        {
            if (FailAttach) throw new InvalidOperationException("Attach failed");
            LastAttachedHost = host;
            events.Add("attach");
            return 0;
        }
        public void Detach(nint host, nint window, nint previousParent)
        {
            if (FailDetach) throw new InvalidOperationException("Detach failed");
            events.Add("detach");
        }
    }
}
