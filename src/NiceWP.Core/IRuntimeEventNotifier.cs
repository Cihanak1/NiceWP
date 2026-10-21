namespace NiceWP.Core;

/// <summary>
/// Provides event notifications for system-level lifecycle events:
/// foreground window change, session lock/unlock, power state, and display power.
/// </summary>
public interface IRuntimeEventNotifier : IDisposable
{
    event EventHandler? ForegroundWindowChanged;
    event Action<string?>? ForegroundProcessChanged;
    event Action<bool>? SessionLockChanged;
    event Action<bool>? DisplayPowerChanged;
    event EventHandler? PowerStatusChanged;

    bool IsForegroundHookActive { get; }
    bool IsSessionNotificationActive { get; }
    bool IsDisplayNotificationActive { get; }
    bool IsPowerNotificationActive { get; }
    string? CurrentForegroundProcessName { get; }
}

/// <summary>
/// No-op event notifier used when native events are disabled or unavailable.
/// </summary>
public sealed class NullRuntimeEventNotifier : IRuntimeEventNotifier
{
    public static readonly NullRuntimeEventNotifier Instance = new();

    public event EventHandler? ForegroundWindowChanged { add { } remove { } }
    public event Action<string?>? ForegroundProcessChanged { add { } remove { } }
    public event Action<bool>? SessionLockChanged { add { } remove { } }
    public event Action<bool>? DisplayPowerChanged { add { } remove { } }
    public event EventHandler? PowerStatusChanged { add { } remove { } }

    public bool IsForegroundHookActive => false;
    public bool IsSessionNotificationActive => false;
    public bool IsDisplayNotificationActive => false;
    public bool IsPowerNotificationActive => false;
    public string? CurrentForegroundProcessName => null;

    public void Dispose() { }
}
