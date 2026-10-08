using System.Diagnostics;

namespace NiceWP.Infrastructure.SingleInstance;

/// <summary>
/// Session-scoped single instance guard using a named mutex for NiceWP.App.
/// Ensures only one UI instance runs per user session.
/// </summary>
public sealed class AppSingleInstanceGuard : IDisposable
{
    public static string GetDefaultMutexName() =>
        $@"Local\NiceWP.App.SingleInstance.{Environment.UserName}";

    private readonly Mutex mutex;
    private bool ownsMutex;
    private bool disposed;

    public bool IsSingleInstance => ownsMutex;

    public AppSingleInstanceGuard(string? customMutexName = null)
    {
        var mutexName = customMutexName ?? GetDefaultMutexName();
        try
        {
            mutex = new Mutex(true, mutexName, out var createdNew);
            ownsMutex = createdNew;
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Failed to initialize app single-instance mutex: {exception}");
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
                Trace.WriteLine($"Failed to release app single-instance mutex: {exception}");
            }
            ownsMutex = false;
        }
        mutex.Dispose();
    }
}
