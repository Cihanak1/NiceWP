using NiceWP.Core;
using NiceWP.Platform.Windows;

namespace NiceWP.Runtime;

public sealed class DesktopHostService(IDesktopHost desktopHost) : IDisposable
{
    private readonly IDesktopHost desktopHost = desktopHost ?? throw new ArgumentNullException(nameof(desktopHost));
    private readonly SemaphoreSlim resolveGate = new(1, 1);
    private readonly object sync = new();
    private nint handle;
    private long generation;
    private bool disposed;
    private bool attaching;
    private (nint Host, nint Window, nint Parent)? attachment;

    public DesktopHostService() : this(new WindowsDesktopHost())
    {
    }

    /// <summary>Call on the window's owning UI thread; the await resumes there for native attachment.</summary>
    public async Task<DesktopHostResult> AttachAsync(nint window, CancellationToken cancellationToken = default)
    {
        long requestedGeneration;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (attaching || attachment != null)
            {
                throw new InvalidOperationException("This service already has a window attachment or an attach in progress.");
            }

            attaching = true;
            requestedGeneration = generation;
        }

        try
        {
            var result = await ResolveAsync(cancellationToken);
            lock (sync)
            {
                CheckGeneration(requestedGeneration);
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (result.Success)
            {
                // The contract accepts only unowned top-level windows (original parent = zero).
                // Retain the pending attachment if rollback fails so Stop can retry cleanup.
                attachment = (result.Handle, window, 0);
                try
                {
                    var parent = desktopHost.Attach(result.Handle, window);
                    attachment = (result.Handle, window, parent);
                }
                catch (Exception attachError)
                {
                    try
                    {
                        desktopHost.Detach(result.Handle, window, 0);
                        attachment = null;
                    }
                    catch (Exception cleanupError)
                    {
                        throw new AggregateException("Desktop attachment and cleanup failed.", attachError, cleanupError);
                    }

                    throw;
                }
            }

            return result;
        }
        finally
        {
            lock (sync)
            {
                attaching = false;
            }
        }
    }

    /// <summary>The last resolved handle, which must be revalidated through ResolveAsync before use.</summary>
    public nint Handle
    {
        get
        {
            lock (sync)
            {
                return handle;
            }
        }
    }

    public async Task<DesktopHostResult> ResolveAsync(CancellationToken cancellationToken = default)
    {
        long requestedGeneration;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            requestedGeneration = generation;
        }

        await resolveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            nint current;
            lock (sync)
            {
                CheckGeneration(requestedGeneration);
                current = handle;
            }

            var result = await Task.Run(async () =>
            {
                if (current != 0 && desktopHost.IsValid(current))
                {
                    return new DesktopHostResult(current, DesktopHostError.None);
                }

                lock (sync)
                {
                    CheckGeneration(requestedGeneration);
                    handle = 0;
                }

                return await desktopHost.ResolveAsync(cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);

            lock (sync)
            {
                CheckGeneration(requestedGeneration);
                cancellationToken.ThrowIfCancellationRequested();
                handle = result.Success ? result.Handle : 0;
                return result;
            }
        }
        catch
        {
            lock (sync)
            {
                if (generation == requestedGeneration)
                {
                    handle = 0;
                }
            }

            throw;
        }
        finally
        {
            resolveGate.Release();
        }
    }

    /// <summary>When attached, call on the window's owning thread before destroying the window.</summary>
    public void Stop()
    {
        lock (sync)
        {
            generation++;
            handle = 0;
        }

        if (attachment is { } current)
        {
            desktopHost.Detach(current.Host, current.Window, current.Parent);
            attachment = null;
        }
    }

    public void Dispose()
    {
        Stop();
        lock (sync)
        {
            disposed = true;
            generation++;
            handle = 0;
        }

        // No wait handle is allocated; keep the semaphore usable by in-flight finally blocks.
    }

    private void CheckGeneration(long requestedGeneration)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (requestedGeneration != generation)
        {
            throw new OperationCanceledException("Desktop host resolution was stopped.");
        }
    }
}
