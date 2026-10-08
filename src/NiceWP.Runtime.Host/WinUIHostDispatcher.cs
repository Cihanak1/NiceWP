using Microsoft.UI.Dispatching;

namespace NiceWP.Runtime.Host;

/// <summary>
/// Dispatches work to the WinUI DispatcherQueue associated with the XAML application thread.
/// </summary>
public sealed class WinUIHostDispatcher : IHostDispatcher
{
    private readonly DispatcherQueue dispatcherQueue;

    public bool HasThreadAccess => dispatcherQueue.HasThreadAccess;

    public WinUIHostDispatcher(DispatcherQueue dispatcherQueue)
    {
        this.dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
    }

    public async Task<T> InvokeAsync<T>(Func<Task<T>> action)
    {
        if (dispatcherQueue.HasThreadAccess)
        {
            return await action().ConfigureAwait(false);
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enqueued = dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var result = await action().ConfigureAwait(false);
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        if (!enqueued)
            throw new InvalidOperationException("Could not enqueue operation to WinUI DispatcherQueue.");

        return await tcs.Task.ConfigureAwait(false);
    }

    public async Task<T> InvokeAsync<T>(Func<T> action)
    {
        if (dispatcherQueue.HasThreadAccess)
        {
            return action();
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enqueued = dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var result = action();
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        if (!enqueued)
            throw new InvalidOperationException("Could not enqueue operation to WinUI DispatcherQueue.");

        return await tcs.Task.ConfigureAwait(false);
    }

    public async Task InvokeAsync(Func<Task> action)
    {
        if (dispatcherQueue.HasThreadAccess)
        {
            await action().ConfigureAwait(false);
            return;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enqueued = dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await action().ConfigureAwait(false);
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        if (!enqueued)
            throw new InvalidOperationException("Could not enqueue operation to WinUI DispatcherQueue.");

        await tcs.Task.ConfigureAwait(false);
    }

    public async Task InvokeAsync(Action action)
    {
        if (dispatcherQueue.HasThreadAccess)
        {
            action();
            return;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enqueued = dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                action();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        if (!enqueued)
            throw new InvalidOperationException("Could not enqueue operation to WinUI DispatcherQueue.");

        await tcs.Task.ConfigureAwait(false);
    }
}
