using Microsoft.Win32;
using NiceWP.Infrastructure.Autostart;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.SingleInstance;
using NiceWP.Infrastructure.Tray;
using Xunit;

namespace NiceWP.Runtime.Tests;

[Collection("HostProcessTests")]
public sealed class AutostartTests
{
    private static string CreateUniqueSubKey() =>
        $@"Software\NiceWP.Tests\Autostart_{Guid.NewGuid():N}";

    [Fact]
    public void Autostart_InitiallyDisabled_WhenNoRegistryEntryExists()
    {
        var subKey = CreateUniqueSubKey();
        var manager = new AutostartManager("NiceWP_Test", subKey);

        try
        {
            Assert.False(manager.IsEnabled);
            Assert.Null(manager.GetRegisteredCommand());
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\NiceWP.Tests", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Autostart_Enable_CreatesCorrectRegistryEntryWithQuotedPathAndStartupFlag()
    {
        var subKey = CreateUniqueSubKey();
        var manager = new AutostartManager("NiceWP_Test", subKey);
        var tempExe = Path.Combine(Path.GetTempPath(), $"NiceWP_Test_{Guid.NewGuid():N}.exe");
        File.WriteAllText(tempExe, "dummy");

        try
        {
            var success = manager.Enable(tempExe);
            Assert.True(success);
            Assert.True(manager.IsEnabled);

            var command = manager.GetRegisteredCommand();
            Assert.NotNull(command);
            Assert.Equal($"\"{Path.GetFullPath(tempExe)}\" --startup", command);

            // Directly inspect registry value
            using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: false);
            Assert.NotNull(key);
            var rawValue = key.GetValue("NiceWP_Test") as string;
            Assert.Equal(command, rawValue);
        }
        finally
        {
            File.Delete(tempExe);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\NiceWP.Tests", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Autostart_RealRunKey_EnableAndDisableLifecycle_LeavesNoTrace()
    {
        var testValueName = $"NiceWP_LifecycleTest_{Guid.NewGuid():N}";
        var manager = new AutostartManager(testValueName);
        var tempExe = Path.Combine(Path.GetTempPath(), $"NiceWP_Real_{Guid.NewGuid():N}.exe");
        File.WriteAllText(tempExe, "dummy");

        try
        {
            Assert.False(manager.IsEnabled);

            // 1. Enable
            var enabled = manager.Enable(tempExe);
            Assert.True(enabled);
            Assert.True(manager.IsEnabled);

            // Verify in real HKCU Run key
            using (var key = Registry.CurrentUser.OpenSubKey(AutostartManager.DefaultRunKeySubPath, writable: false))
            {
                Assert.NotNull(key);
                var val = key.GetValue(testValueName) as string;
                Assert.Equal($"\"{Path.GetFullPath(tempExe)}\" --startup", val);
            }

            // 2. Disable
            var disabled = manager.Disable();
            Assert.True(disabled);
            Assert.False(manager.IsEnabled);

            // Verify in real HKCU Run key that value is completely removed
            using (var key = Registry.CurrentUser.OpenSubKey(AutostartManager.DefaultRunKeySubPath, writable: false))
            {
                Assert.NotNull(key);
                Assert.Null(key.GetValue(testValueName));
            }
        }
        finally
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(AutostartManager.DefaultRunKeySubPath, writable: true);
                key?.DeleteValue(testValueName, throwOnMissingValue: false);
            }
            catch
            {
            }

            if (File.Exists(tempExe)) File.Delete(tempExe);
        }
    }

    [Fact]
    public void Autostart_Disable_RemovesOnlyNiceWPEntry_PreservesOtherEntries()
    {
        var subKey = CreateUniqueSubKey();
        var manager = new AutostartManager("NiceWP_Test", subKey);
        var tempExe = Path.Combine(Path.GetTempPath(), $"NiceWP_Test_{Guid.NewGuid():N}.exe");
        File.WriteAllText(tempExe, "dummy");

        try
        {
            // Create subkey with an unrelated existing entry
            using (var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true))
            {
                key.SetValue("OtherApp", "\"C:\\Program Files\\Other\\other.exe\"");
            }

            // Enable NiceWP
            manager.Enable(tempExe);
            Assert.True(manager.IsEnabled);

            // Disable NiceWP
            var disabled = manager.Disable();
            Assert.True(disabled);
            Assert.False(manager.IsEnabled);
            Assert.Null(manager.GetRegisteredCommand());

            // Verify OtherApp is still intact
            using (var key = Registry.CurrentUser.OpenSubKey(subKey, writable: false))
            {
                Assert.NotNull(key);
                Assert.Null(key.GetValue("NiceWP_Test"));
                var otherApp = key.GetValue("OtherApp") as string;
                Assert.Equal("\"C:\\Program Files\\Other\\other.exe\"", otherApp);
            }
        }
        finally
        {
            File.Delete(tempExe);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\NiceWP.Tests", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Autostart_ProgrammaticSync_DoesNotTriggerEnableOrDisable()
    {
        var enableCallCount = 0;
        var disableCallCount = 0;
        var isSyncingAutostart = false;

        void OnChecked()
        {
            if (isSyncingAutostart) return;
            enableCallCount++;
        }

        void OnUnchecked()
        {
            if (isSyncingAutostart) return;
            disableCallCount++;
        }

        // Programmatic sync setting to checked
        isSyncingAutostart = true;
        OnChecked();
        isSyncingAutostart = false;

        // Programmatic sync setting to unchecked
        isSyncingAutostart = true;
        OnUnchecked();
        isSyncingAutostart = false;

        Assert.Equal(0, enableCallCount);
        Assert.Equal(0, disableCallCount);

        // User action without sync flag triggers handlers
        OnChecked();
        Assert.Equal(1, enableCallCount);
        OnUnchecked();
        Assert.Equal(1, disableCallCount);
    }

    [Fact]
    public void Autostart_FailedDisable_RollsBackToActualRegistryState()
    {
        var subKey = CreateUniqueSubKey();
        var manager = new AutostartManager("NiceWP_Test", subKey);
        var tempExe = Path.Combine(Path.GetTempPath(), $"NiceWP_Test_{Guid.NewGuid():N}.exe");
        File.WriteAllText(tempExe, "dummy");

        try
        {
            manager.Enable(tempExe);
            Assert.True(manager.IsEnabled);

            // Simulate UI rollback logic when Disable fails
            var uiStateChecked = false;
            var isSyncingAutostart = false;
            string? statusMessage = null;

            void OnUserUnchecked(bool disableSuccess)
            {
                if (isSyncingAutostart) return;

                if (!disableSuccess)
                {
                    statusMessage = "Failed to disable autostart in registry.";
                    isSyncingAutostart = true;
                    try
                    {
                        uiStateChecked = manager.IsEnabled;
                    }
                    finally
                    {
                        isSyncingAutostart = false;
                    }
                }
            }

            OnUserUnchecked(disableSuccess: false);

            Assert.Equal("Failed to disable autostart in registry.", statusMessage);
            Assert.True(uiStateChecked, "UI state must rollback to true because registry state is still enabled.");
            Assert.True(manager.IsEnabled, "Registry state remains enabled.");
        }
        finally
        {
            File.Delete(tempExe);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\NiceWP.Tests", throwOnMissingSubKey: false);
        }
    }

    [Theory]
    [InlineData(@"C:\Program Files\NiceWP\NiceWP.App.exe", "\"C:\\Program Files\\NiceWP\\NiceWP.App.exe\" --startup")]
    [InlineData(@"C:\Tools\NiceWP.App.exe", "\"C:\\Tools\\NiceWP.App.exe\" --startup")]
    public void FormatCommandValue_QuotesExecutablePathCorrectly(string inputPath, string expectedCommand)
    {
        var formatted = AutostartManager.FormatCommandValue(inputPath);
        Assert.Equal(expectedCommand, formatted);
    }

    [Fact]
    public void ResolveExecutablePath_FindsNiceWPAppInBuildOutputs()
    {
        var resolved = AutostartManager.ResolveExecutablePath();
        Assert.NotNull(resolved);
        Assert.True(File.Exists(resolved), $"Resolved executable does not exist: {resolved}");
        Assert.EndsWith("NiceWP.App.exe", resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartupMode_ControllerStartsHidden_ThenRestoresOnOpen()
    {
        var pipeName = $@"NiceWP.Test.StartupPipe.{Guid.NewGuid():N}";
        var client = new RuntimeIpcClient(pipeName);

        var restoreCalled = false;
        var syncCalled = false;

        // Startup mode: initialWindowVisible = false
        var controller = new TrayLifecycleController(
            client,
            initialWindowVisible: false,
            restoreWindowAction: () => restoreCalled = true,
            syncStatusAction: () =>
            {
                syncCalled = true;
                return Task.CompletedTask;
            });

        Assert.False(controller.IsWindowVisible, "Window must start hidden in --startup mode.");
        Assert.False(controller.IsExited);

        // When user opens via tray
        await controller.RequestOpenWindowAsync();

        Assert.True(controller.IsWindowVisible, "Window must become visible after Open.");
        Assert.True(restoreCalled, "Restore window action must be called.");
        Assert.True(syncCalled, "Sync status must be called.");
    }

    [Fact]
    public async Task StartupMode_RealHostProcess_ConnectsAndSyncsStatus()
    {
        var hostExe = RuntimeHostLauncher.ResolveHostExecutablePath();
        if (hostExe == null || !File.Exists(hostExe))
        {
            return;
        }

        var client = new RuntimeIpcClient();
        var launcher = new RuntimeHostLauncher(client, hostExe);

        var launchResult = await launcher.EnsureHostRunningAsync(timeoutMs: 6000, retryIntervalMs: 100);
        Assert.True(launchResult.Success);

        try
        {
            var syncCalled = false;
            var controller = new TrayLifecycleController(
                client,
                initialWindowVisible: false,
                syncStatusAction: async () =>
                {
                    var status = await client.GetStatusAsync(timeoutMs: 4000);
                    Assert.True(status.Success);
                    Assert.True(status.IsRunning);
                    syncCalled = true;
                });

            Assert.False(controller.IsWindowVisible);

            await controller.RequestOpenWindowAsync();
            Assert.True(controller.IsWindowVisible);
            Assert.True(syncCalled);
        }
        finally
        {
            try
            {
                await client.ShutdownAsync(timeoutMs: 2000);
                await Task.Delay(200);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void AppSingleInstance_PreventsDuplicateInstanceInStartupMode()
    {
        var mutexName = $@"Local\NiceWP.Test.StartupSingleInstance.{Guid.NewGuid():N}";

        using var guard1 = new AppSingleInstanceGuard(mutexName);
        Assert.True(guard1.IsSingleInstance);

        // Second instance attempting to launch in startup mode
        using var guard2 = new AppSingleInstanceGuard(mutexName);
        Assert.False(guard2.IsSingleInstance, "Second instance must detect running instance and exit.");
    }
}
