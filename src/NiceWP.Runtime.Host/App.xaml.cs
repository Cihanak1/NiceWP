using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace NiceWP.Runtime.Host;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private RuntimeHostApp? hostApp;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var logPath = Path.Combine(Path.GetTempPath(), $"NiceWP-runtime-host-{Environment.ProcessId}.log");
            Trace.Listeners.Add(new TextWriterTraceListener(logPath));
            Trace.AutoFlush = true;
        }
        catch
        {
            // Ignore diagnostic log setup errors
        }

        var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        var dispatcher = new WinUIHostDispatcher(dispatcherQueue);

        hostApp = new RuntimeHostApp(dispatcher: dispatcher);
        if (!hostApp.IsSingleInstance)
        {
            Trace.WriteLine("NiceWP.Runtime.Host is already running. Exiting.");
            hostApp.Dispose();
            Exit();
            return;
        }

        hostApp.Start();
    }
}
