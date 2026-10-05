using NiceWP.Core;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class DesktopHostServiceTests
{
    [Fact]
    public async Task ReusesAValidHost()
    {
        var host = new FakeDesktopHost();
        using var service = new DesktopHostService(host);

        Assert.Equal((nint)10, (await service.ResolveAsync()).Handle);
        Assert.Equal((nint)10, (await service.ResolveAsync()).Handle);
        Assert.Equal((nint)10, service.Handle);
        Assert.Equal(1, host.ResolveCount);
    }

    [Fact]
    public async Task ResolvesAgainAfterExplorerRestart()
    {
        var host = new FakeDesktopHost();
        using var service = new DesktopHostService(host);
        await service.ResolveAsync();
        host.ValidHandle = 20;
        host.Result = new(20, DesktopHostError.None);

        Assert.Equal((nint)20, (await service.ResolveAsync()).Handle);
        Assert.Equal((nint)20, service.Handle);
        Assert.Equal(2, host.ResolveCount);
    }

    [Theory]
    [InlineData(DesktopHostError.ExplorerUnavailable)]
    [InlineData(DesktopHostError.WorkerUnavailable)]
    [InlineData(DesktopHostError.ShellUnresponsive)]
    [InlineData(DesktopHostError.NativeFailure)]
    public async Task FailedRediscoveryClearsStaleHandleAndPreservesError(DesktopHostError error)
    {
        var host = new FakeDesktopHost();
        using var service = new DesktopHostService(host);
        await service.ResolveAsync();
        host.ValidHandle = 0;
        host.Result = new(0, error, 123);

        Assert.Equal(host.Result, await service.ResolveAsync());
        Assert.Equal((nint)0, service.Handle);
    }

    [Fact]
    public async Task ConcurrentRequestsShareResolution()
    {
        var host = new FakeDesktopHost { Pending = NewCompletion() };
        using var service = new DesktopHostService(host);
        var first = service.ResolveAsync();
        await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.ResolveAsync();
        host.Pending.SetResult(host.Result);

        var results = await Task.WhenAll(first, second);
        Assert.All(results, result => Assert.True(result.Success));
        Assert.Equal(1, host.ResolveCount);
    }

    [Fact]
    public async Task StopClearsReferenceAndAllowsNewResolution()
    {
        var host = new FakeDesktopHost();
        using var service = new DesktopHostService(host);
        await service.ResolveAsync();

        service.Stop();
        Assert.Equal((nint)0, service.Handle);
        Assert.True((await service.ResolveAsync()).Success);
        Assert.Equal(2, host.ResolveCount);
    }

    [Fact]
    public async Task StopRejectsInFlightAndQueuedResults()
    {
        var host = new FakeDesktopHost { Pending = NewCompletion() };
        using var service = new DesktopHostService(host);
        var first = service.ResolveAsync();
        await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = service.ResolveAsync();
        service.Stop();
        host.Pending.SetResult(host.Result);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal((nint)0, service.Handle);
        Assert.Equal(1, host.ResolveCount);
    }

    [Fact]
    public async Task DisposeRejectsInFlightAndFutureResolution()
    {
        var host = new FakeDesktopHost { Pending = NewCompletion() };
        var service = new DesktopHostService(host);
        var pending = service.ResolveAsync();
        await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        service.Dispose();
        service.Dispose();
        host.Pending.SetResult(host.Result);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.ResolveAsync());
        Assert.Equal((nint)0, service.Handle);
    }

    [Fact]
    public async Task CancellationDoesNotPublishResultAndReleasesGate()
    {
        var host = new FakeDesktopHost { Pending = NewCompletion() };
        using var service = new DesktopHostService(host);
        using var cancellation = new CancellationTokenSource();
        var pending = service.ResolveAsync(cancellation.Token);
        await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        host.Pending.SetResult(host.Result);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal((nint)0, service.Handle);
        Assert.True((await service.ResolveAsync()).Success);
    }

    [Fact]
    public async Task NativeExceptionIsPropagatedAndStaleHandleIsCleared()
    {
        var host = new FakeDesktopHost();
        using var service = new DesktopHostService(host);
        await service.ResolveAsync();
        host.ValidHandle = 0;
        host.Pending = NewCompletion();
        host.Pending.SetException(new InvalidOperationException("Native failure"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolveAsync());
        Assert.Equal("Native failure", exception.Message);
        Assert.Equal((nint)0, service.Handle);
        host.Pending = null;
        Assert.True((await service.ResolveAsync()).Success);
    }

    private static TaskCompletionSource<DesktopHostResult> NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task AttachesOnceAndStopDetachesOnce()
    {
        var host = new FakeDesktopHost();
        using var service = new DesktopHostService(host);
        Assert.True((await service.AttachAsync(42)).Success);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AttachAsync(42));
        service.Stop();
        service.Stop();

        Assert.Equal(1, host.AttachCount);
        Assert.Equal(1, host.DetachCount);
        Assert.Equal(((nint)10, (nint)42, (nint)0), host.LastDetached);
        Assert.Equal((nint)0, service.Handle);
    }

    [Fact]
    public async Task StopWhileResolvingPreventsAttachment()
    {
        var host = new FakeDesktopHost { Pending = NewCompletion() };
        using var service = new DesktopHostService(host);
        var pending = service.AttachAsync(42);
        await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.Stop();
        host.Pending.SetResult(host.Result);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, host.AttachCount);
    }

    [Fact]
    public async Task ResolveFailureDoesNotAttach()
    {
        var host = new FakeDesktopHost { Result = new(0, DesktopHostError.WorkerUnavailable) };
        using var service = new DesktopHostService(host);

        Assert.False((await service.AttachAsync(42)).Success);
        Assert.Equal(0, host.AttachCount);
    }

    [Fact]
    public async Task FailedAttachmentRollsBackAndCanBeRetried()
    {
        var host = new FakeDesktopHost { AttachFailure = new InvalidOperationException("Attach failed") };
        using var service = new DesktopHostService(host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AttachAsync(42));
        Assert.Equal(1, host.DetachCount);
        host.AttachFailure = null;
        Assert.True((await service.AttachAsync(42)).Success);
    }

    [Fact]
    public async Task DisposeRetriesFailedDetachAndRetainsOriginalAttachment()
    {
        var host = new FakeDesktopHost();
        var service = new DesktopHostService(host);
        await service.AttachAsync(42);
        host.DetachFailure = new InvalidOperationException("Detach failed");

        Assert.Throws<InvalidOperationException>(() => service.Stop());
        host.DetachFailure = null;
        service.Dispose();
        Assert.Equal(2, host.DetachCount);
        Assert.Equal(((nint)10, (nint)42, (nint)0), host.LastDetached);
    }

    private sealed class FakeDesktopHost : IDesktopHost
    {
        public nint ValidHandle { get; set; } = 10;
        public DesktopHostResult Result { get; set; } = new(10, DesktopHostError.None);
        public int ResolveCount { get; private set; }
        public int AttachCount { get; private set; }
        public int DetachCount { get; private set; }
        public Exception? AttachFailure { get; set; }
        public Exception? DetachFailure { get; set; }
        public (nint Host, nint Window, nint Parent) LastDetached { get; private set; }
        public TaskCompletionSource<DesktopHostResult>? Pending { get; set; }
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DesktopHostResult> ResolveAsync(CancellationToken cancellationToken = default)
        {
            ResolveCount++;
            Entered.TrySetResult(true);
            return Pending?.Task ?? Task.FromResult(Result);
        }

        public bool IsValid(nint handle) => handle != 0 && handle == ValidHandle;

        public nint Attach(nint host, nint window)
        {
            AttachCount++;
            if (AttachFailure != null)
            {
                throw AttachFailure;
            }

            return 0;
        }

        public void Detach(nint host, nint window, nint previousParent)
        {
            DetachCount++;
            LastDetached = (host, window, previousParent);
            if (DetachFailure != null)
            {
                throw DetachFailure;
            }
        }
    }
}
