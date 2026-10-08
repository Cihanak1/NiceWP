using System.Diagnostics;

namespace NiceWP.Runtime.Host;

/// <summary>
/// Session-scoped single instance guard using an atomic named mutex.
/// Ensures only one runtime host runs per user session.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    public const string DefaultMutexName = @"Local\NiceWP.Runtime.Host.Mutex";
    private readonly Mutex mutex;
    private bool ownsMutex;
    private bool disposed;

    public bool IsSingleInstance => ownsMutex;

    public SingleInstanceGuard(string mutexName = DefaultMutexName)
    {
        try
        {
            mutex = new Mutex(true, mutexName, out var createdNew);
            ownsMutex = createdNew;
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Failed to initialize single-instance mutex: {exception}");
            mutex = new Mutex(false, mutexName);
            ownsMutex = false;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (ownsMutex)
        {
            try
            {
                mutex.ReleaseMutex();
            }
            catch (Exception exception)
            {
                Trace.WriteLine($"Failed to release single-instance mutex: {exception}");
            }
            ownsMutex = false;
        }
        mutex.Dispose();
    }
}
