using System;
using System.Linq;
using Microsoft.UI.Xaml;
using NiceWP.App.Tray;
using NiceWP.Infrastructure.Autostart;
using NiceWP.Infrastructure.SingleInstance;

namespace NiceWP.App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;
    private static AppSingleInstanceGuard? _singleInstanceGuard;

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Releases the session single instance mutex upon graceful exit.
    /// </summary>
    public static void ReleaseSingleInstance()
    {
        _singleInstanceGuard?.Dispose();
        _singleInstanceGuard = null;
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _singleInstanceGuard = new AppSingleInstanceGuard();
        if (!_singleInstanceGuard.IsSingleInstance)
        {
            // Another instance of NiceWP.App is already running in this user session.
            // Signal the existing instance to unhide / restore its window, then terminate immediately.
            TrayInterop.SignalExistingAppInstance();
            Environment.Exit(0);
            return;
        }

        var isStartup = Environment.GetCommandLineArgs()
            .Any(arg => string.Equals(arg, AutostartManager.StartupArgument, StringComparison.OrdinalIgnoreCase));

        _window = new DesktopTestWindow(isStartup: isStartup);
        if (!isStartup)
        {
            _window.Activate();
        }
    }
}
