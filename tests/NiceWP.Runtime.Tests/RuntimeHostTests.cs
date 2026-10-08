using NiceWP.Runtime.Host;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class RuntimeHostTests
{
    [Fact]
    public void SingleInstanceGuard_FirstInstanceAcquiresMutex()
    {
        var mutexName = $@"Local\NiceWP.Test.{Guid.NewGuid():N}";
        using var guard = new SingleInstanceGuard(mutexName);

        Assert.True(guard.IsSingleInstance);
    }

    [Fact]
    public async Task SingleInstanceGuard_SecondInstanceIsPrevented()
    {
        var mutexName = $@"Local\NiceWP.Test.{Guid.NewGuid():N}";
        using var first = new SingleInstanceGuard(mutexName);
        Assert.True(first.IsSingleInstance);

        var secondIsSingleInstance = await Task.Run(() =>
        {
            using var second = new SingleInstanceGuard(mutexName);
            return second.IsSingleInstance;
        });

        Assert.False(secondIsSingleInstance);
    }

    [Fact]
    public void SingleInstanceGuard_AllowsNewInstanceAfterDispose()
    {
        var mutexName = $@"Local\NiceWP.Test.{Guid.NewGuid():N}";

        using (var first = new SingleInstanceGuard(mutexName))
        {
            Assert.True(first.IsSingleInstance);
        }

        using (var second = new SingleInstanceGuard(mutexName))
        {
            Assert.True(second.IsSingleInstance);
        }
    }

    [Fact]
    public void RuntimeCompositionRoot_InitializesServicesWithoutStartingWallpaper()
    {
        using var root = new RuntimeCompositionRoot();

        Assert.NotNull(root.DesktopHost);
        Assert.NotNull(root.WallpaperService);
        Assert.Null(root.WallpaperService.ActivePath);
        Assert.False(root.IsDisposed);
    }

    [Fact]
    public void RuntimeCompositionRoot_DisposesSafely()
    {
        var root = new RuntimeCompositionRoot();
        Assert.False(root.IsDisposed);

        root.Dispose();
        Assert.True(root.IsDisposed);

        // Multiple disposes should not throw
        var exception = Record.Exception(() => root.Dispose());
        Assert.Null(exception);
    }

    [Fact]
    public async Task RuntimeHostApp_ExitsWithCode1WhenNotSingleInstance()
    {
        var mutexName = $@"Local\NiceWP.Test.{Guid.NewGuid():N}";
        using var activeInstance = new SingleInstanceGuard(mutexName);
        Assert.True(activeInstance.IsSingleInstance);

        var exitCode = await Task.Run(() =>
        {
            using var conflictingGuard = new SingleInstanceGuard(mutexName);
            Assert.False(conflictingGuard.IsSingleInstance);

            using var app = new RuntimeHostApp(guard: conflictingGuard);
            return app.Run();
        });

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void RuntimeHostApp_ShutsDownGracefullyOnCancellation()
    {
        var mutexName = $@"Local\NiceWP.Test.{Guid.NewGuid():N}";
        using var guard = new SingleInstanceGuard(mutexName);
        using var app = new RuntimeHostApp(guard: guard);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel to trigger immediate graceful shutdown

        var exitCode = app.Run(cts.Token);
        Assert.Equal(0, exitCode);
    }
}
