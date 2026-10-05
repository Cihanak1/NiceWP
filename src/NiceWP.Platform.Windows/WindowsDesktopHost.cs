using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NiceWP.Core;
using static NiceWP.Platform.Windows.DesktopNativeMethods;

namespace NiceWP.Platform.Windows;

public sealed class WindowsDesktopHost : IDesktopHost
{
    private static readonly SemaphoreSlim ResolveGate = new(1, 1);
    private static (nint Handle, uint Process) preparedExplorer;
    private static DesktopHostResult? preparationFailure;
    private readonly ConcurrentDictionary<nint, WindowState> attachments = new();

    private sealed record WindowState(nint Host, nint Parent, nint Style, WindowRect Bounds);

    public async Task<DesktopHostResult> ResolveAsync(CancellationToken cancellationToken = default)
    {
        await ResolveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Resolve(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ResolveGate.Release();
        }
    }

    public bool IsValid(nint handle)
    {
        if (handle == 0 || !IsWindow(handle))
        {
            return false;
        }

        var explorer = FindExplorer();
        return explorer.Handle != 0 && FindHost(explorer) == handle;
    }

    public nint Attach(nint host, nint window)
    {
        ValidateWindow(window);
        if (!IsValid(host) || window == host)
        {
            throw new InvalidOperationException("The desktop host is no longer available.");
        }

        var previousParent = GetParent(window);
        if (previousParent == host || attachments.ContainsKey(window))
        {
            throw new InvalidOperationException("The window is already attached; retain its original parent.");
        }

        GetWindowThreadProcessId(previousParent, out var process);
        if (previousParent != 0 && process != (uint)Environment.ProcessId)
        {
            throw new InvalidOperationException("The original parent must belong to this process.");
        }

        var style = GetWindowLongPtr(window, StyleIndex);
        var error = Marshal.GetLastPInvokeError();
        if (style == 0 && error != 0)
        {
            throw new Win32Exception(error, "Reading the original window style failed.");
        }

        if ((style.ToInt64() & ChildStyle) != 0 || previousParent != 0)
        {
            throw new InvalidOperationException("Desktop attachment requires an unowned top-level window.");
        }

        if (!GetWindowRect(window, out var bounds))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Reading the original window bounds failed.");
        }

        var state = new WindowState(host, previousParent, style, bounds);
        attachments[window] = state;
        try
        {
            SetStyle(window, (nint)((style.ToInt64() | ChildStyle) & ~(PopupStyle | WindowFrameStyle)));
            ChangeParent(window, host);
            if (!SetWindowPos(window, 0, 0, 0, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
                FrameChanged | NoActivate | NoZOrder))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Positioning the desktop test window failed.");
            }

            Trace.WriteLine($"Desktop attach: window=0x{window:X}, host=0x{host:X}, parent=0x{GetParent(window):X}");
            return previousParent;
        }
        catch (Exception attachError)
        {
            try
            {
                Restore(window, state);
                attachments.TryRemove(window, out _);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException("Desktop attach and rollback failed.", attachError, rollbackError);
            }

            throw;
        }
    }

    public void Detach(nint host, nint window, nint previousParent)
    {
        if (!attachments.TryGetValue(window, out var state))
        {
            return;
        }

        if (state.Host != host || state.Parent != previousParent)
        {
            throw new InvalidOperationException("The supplied attachment does not match the saved window state.");
        }

        if (IsWindow(window))
        {
            ValidateWindow(window);
            Restore(window, state);
            Trace.WriteLine($"Desktop cleanup: window=0x{window:X}, parent=0x{GetParent(window):X}, styleRestored={GetWindowLongPtr(window, StyleIndex) == state.Style}");
        }
        else
        {
            Trace.WriteLine($"Desktop cleanup: window=0x{window:X} already destroyed.");
        }

        attachments.TryRemove(window, out _);
    }

    private static DesktopHostResult Resolve(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var explorer = FindExplorer();
        if (explorer.Handle == 0)
        {
            preparedExplorer = default;
            preparationFailure = null;
            return new(0, DesktopHostError.ExplorerUnavailable);
        }

        try
        {
            var host = FindHost(explorer);
            if (host != 0)
            {
                return new(host, DesktopHostError.None);
            }

            if (preparedExplorer != explorer)
            {
                preparedExplorer = explorer;
                preparationFailure = null;
                if (SendMessageTimeout(explorer.Handle, PrepareWorkerMessage, 0, 0,
                    AbortIfHung | ErrorOnExit, MessageTimeoutMilliseconds, out _) == 0)
                {
                    preparationFailure = new(0, DesktopHostError.ShellUnresponsive, Marshal.GetLastPInvokeError());
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (FindExplorer() != explorer)
            {
                return new(0, DesktopHostError.ExplorerUnavailable);
            }

            host = FindHost(explorer);
            return host != 0
                ? new(host, DesktopHostError.None)
                : preparationFailure ?? new DesktopHostResult(0, DesktopHostError.WorkerUnavailable);
        }
        catch (Win32Exception exception)
        {
            return new(0, DesktopHostError.NativeFailure, exception.NativeErrorCode);
        }
    }

    private static (nint Handle, uint Process) FindExplorer()
    {
        var progman = FindWindow(ProgmanClass, null);
        return progman != 0 && IsWindow(progman)
            && GetWindowThreadProcessId(progman, out var process) != 0
            ? (progman, process) : default;
    }

    private static nint FindHost((nint Handle, uint Process) explorer)
    {
        // Current Explorer versions can keep both the icon view and WorkerW under Progman.
        var desktopView = FindWindowEx(explorer.Handle, 0, DesktopViewClass, null);
        if (desktopView != 0 && BelongsToExplorer(desktopView, explorer.Process))
        {
            var childWorker = FindWindowEx(explorer.Handle, desktopView, WorkerClass, null);
            if (childWorker != 0 && GetParent(childWorker) == explorer.Handle
                && BelongsToExplorer(childWorker, explorer.Process)
                && FindWindowEx(childWorker, 0, DesktopViewClass, null) == 0
                && FindExplorer() == explorer)
            {
                Trace.WriteLine($"Desktop host: Progman=0x{explorer.Handle:X}, iconView=0x{desktopView:X}, childWorker=0x{childWorker:X} behind icon view.");
                return childWorker;
            }
        }

        nint host = 0;
        var enumerated = EnumWindows((window, _) =>
        {
            if (host != 0 || !BelongsToExplorer(window, explorer.Process)
                || FindWindowEx(window, 0, DesktopViewClass, null) == 0)
            {
                return true;
            }

            // Search siblings behind the icon owner, validating each candidate instead of
            // assuming the first WorkerW (or a fixed enumeration index) is the host.
            var candidate = FindWindowEx(0, window, WorkerClass, null);
            while (candidate != 0)
            {
                if (GetParent(candidate) == 0 && BelongsToExplorer(candidate, explorer.Process)
                    && FindWindowEx(candidate, 0, DesktopViewClass, null) == 0)
                {
                    host = candidate;
                    break;
                }

                candidate = FindWindowEx(0, candidate, WorkerClass, null);
            }

            return true;
        }, 0);

        if (!enumerated)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Desktop window enumeration failed.");
        }

        return host != 0 && IsWindow(host) && FindExplorer() == explorer ? host : 0;
    }

    private static bool BelongsToExplorer(nint window, uint process) =>
        IsWindow(window) && GetWindowThreadProcessId(window, out var owner) != 0 && owner == process;

    private static void ValidateWindow(nint window)
    {
        var thread = GetWindowThreadProcessId(window, out var process);
        if (!IsWindow(window) || process != (uint)Environment.ProcessId || thread != GetCurrentThreadId())
        {
            throw new InvalidOperationException("The window must belong to the calling thread in this process.");
        }
    }

    private static void SetStyle(nint window, nint style)
    {
        var previous = SetWindowLongPtr(window, StyleIndex, style);
        var error = Marshal.GetLastPInvokeError();
        if (previous == 0 && error != 0)
        {
            throw new Win32Exception(error, "Changing the window style failed.");
        }
    }

    private static void Restore(nint window, WindowState state)
    {
        // Restore even if Explorer has disappeared: a top-level window's saved parent is zero.
        if (GetParent(window) != state.Parent)
        {
            ChangeParent(window, state.Parent);
        }

        SetStyle(window, state.Style);
        var bounds = state.Bounds;
        if (!SetWindowPos(window, 0, bounds.Left, bounds.Top, bounds.Right - bounds.Left,
            bounds.Bottom - bounds.Top, FrameChanged | NoActivate | NoZOrder))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Restoring the window bounds failed.");
        }

        if (GetParent(window) != state.Parent)
        {
            throw new InvalidOperationException("The original window parent could not be restored.");
        }
    }

    private static void ChangeParent(nint window, nint parent)
    {
        var previousParent = SetParent(window, parent);
        var error = Marshal.GetLastPInvokeError();
        // A zero previous parent alone does not indicate failure.
        if (previousParent == 0 && error != 0)
        {
            throw new Win32Exception(error, "Changing the desktop child parent failed.");
        }

        // With WS_CHILD still set, SetParent(NULL) makes GetParent return the desktop HWND.
        // Restore validates the zero parent after restoring the top-level style.
        if (parent != 0 && GetParent(window) != parent)
        {
            throw new InvalidOperationException("The window parent changed during the desktop operation.");
        }
    }
}
