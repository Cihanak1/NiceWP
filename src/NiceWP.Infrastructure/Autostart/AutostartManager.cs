using System.Diagnostics;
using Microsoft.Win32;

namespace NiceWP.Infrastructure.Autostart;

/// <summary>
/// Manages Windows login autostart registration for NiceWP using the user-scoped HKCU Run key.
/// Never writes to HKLM, never requires administrative elevation or UAC, and only manages NiceWP's entry.
/// </summary>
public sealed class AutostartManager
{
    public const string DefaultValueName = "NiceWP";
    public const string StartupArgument = "--startup";
    public const string DefaultRunKeySubPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string ValueName { get; }
    public string RunKeySubPath { get; }

    public AutostartManager(string? valueName = null, string? runKeySubPath = null)
    {
        ValueName = valueName ?? DefaultValueName;
        RunKeySubPath = runKeySubPath ?? DefaultRunKeySubPath;
    }

    /// <summary>
    /// Gets whether NiceWP autostart is currently registered and enabled in HKCU.
    /// </summary>
    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeySubPath, writable: false);
                if (key == null) return false;

                var value = key.GetValue(ValueName) as string;
                return !string.IsNullOrWhiteSpace(value);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to query autostart registry state: {ex}");
                return false;
            }
        }
    }

    /// <summary>
    /// Registers NiceWP in the user's HKCU Run key to launch with the --startup flag on Windows login.
    /// </summary>
    /// <param name="executablePath">Optional custom path to the NiceWP.App executable. If null, automatically resolved.</param>
    /// <returns>True if successfully registered, false otherwise.</returns>
    public bool Enable(string? executablePath = null)
    {
        try
        {
            var resolvedPath = ResolveExecutablePath(executablePath);
            if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
            {
                Trace.WriteLine($"Cannot enable autostart: executable path not found '{resolvedPath}'.");
                return false;
            }

            var commandValue = FormatCommandValue(resolvedPath);

            using var key = Registry.CurrentUser.CreateSubKey(RunKeySubPath, writable: true);
            if (key == null)
            {
                Trace.WriteLine("Cannot enable autostart: unable to open HKCU Run key for write access.");
                return false;
            }

            key.SetValue(ValueName, commandValue, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to enable autostart: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Unregisters NiceWP from the user's HKCU Run key. Only deletes NiceWP's value.
    /// </summary>
    /// <returns>True if successfully unregistered or already disabled, false on unexpected error.</returns>
    public bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeySubPath, writable: true);
            if (key == null) return true;

            key.DeleteValue(ValueName, throwOnMissingValue: false);

            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to disable autostart: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Returns the raw command string registered under the autostart value, or null if none.
    /// </summary>
    public string? GetRegisteredCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeySubPath, writable: false);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to read registered autostart command: {ex}");
            return null;
        }
    }

    /// <summary>
    /// Formats the command value string ensuring the executable path is properly quoted and followed by --startup.
    /// </summary>
    public static string FormatCommandValue(string exePath)
    {
        var fullPath = Path.GetFullPath(exePath);
        return $"\"{fullPath}\" {StartupArgument}";
    }

    /// <summary>
    /// Resolves the absolute path to NiceWP.App.exe without relying on hardcoded user directories.
    /// </summary>
    public static string? ResolveExecutablePath(string? customPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            return File.Exists(customPath) ? Path.GetFullPath(customPath) : null;
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
        {
            if (Path.GetFileName(processPath).Equals("NiceWP.App.exe", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(processPath);
            }
        }

        var baseDir = AppContext.BaseDirectory;
        var directCandidate = Path.Combine(baseDir, "NiceWP.App.exe");
        if (File.Exists(directCandidate))
        {
            return Path.GetFullPath(directCandidate);
        }

        var repoCandidates = new[]
        {
            Path.Combine(baseDir, @"..\..\..\..\..\..\src\NiceWP.App\bin\x64\Debug\net10.0-windows10.0.19041.0\NiceWP.App.exe"),
            Path.Combine(baseDir, @"..\..\..\..\..\..\src\NiceWP.App\bin\x64\Release\net10.0-windows10.0.19041.0\NiceWP.App.exe"),
            Path.Combine(baseDir, @"..\..\..\..\..\..\src\NiceWP.App\bin\Debug\net10.0-windows10.0.19041.0\NiceWP.App.exe"),
            Path.Combine(baseDir, @"..\..\..\..\..\..\src\NiceWP.App\bin\Release\net10.0-windows10.0.19041.0\NiceWP.App.exe"),
            Path.Combine(baseDir, @"..\..\..\..\..\src\NiceWP.App\bin\x64\Debug\net10.0-windows10.0.19041.0\NiceWP.App.exe"),
            Path.Combine(baseDir, @"..\..\..\..\..\src\NiceWP.App\bin\x64\Release\net10.0-windows10.0.19041.0\NiceWP.App.exe"),
            Path.Combine(baseDir, @"..\..\..\..\src\NiceWP.App\bin\x64\Debug\net10.0-windows10.0.19041.0\NiceWP.App.exe"),
            Path.Combine(baseDir, @"..\..\..\..\src\NiceWP.App\bin\x64\Release\net10.0-windows10.0.19041.0\NiceWP.App.exe")
        };

        foreach (var candidate in repoCandidates)
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch
            {
                // Ignore resolution errors
            }
        }

        // Upward directory search
        try
        {
            var current = new DirectoryInfo(baseDir);
            for (int i = 0; i < 7 && current != null; i++)
            {
                var probePaths = new[]
                {
                    Path.Combine(current.FullName, "NiceWP.App.exe"),
                    Path.Combine(current.FullName, "src", "NiceWP.App", "bin", "x64", "Debug", "net10.0-windows10.0.19041.0", "NiceWP.App.exe"),
                    Path.Combine(current.FullName, "src", "NiceWP.App", "bin", "x64", "Release", "net10.0-windows10.0.19041.0", "NiceWP.App.exe"),
                    Path.Combine(current.FullName, "src", "NiceWP.App", "bin", "Debug", "net10.0-windows10.0.19041.0", "NiceWP.App.exe"),
                    Path.Combine(current.FullName, "src", "NiceWP.App", "bin", "Release", "net10.0-windows10.0.19041.0", "NiceWP.App.exe"),
                    Path.Combine(current.FullName, "NiceWP.App", "bin", "x64", "Debug", "net10.0-windows10.0.19041.0", "NiceWP.App.exe"),
                    Path.Combine(current.FullName, "NiceWP.App", "bin", "x64", "Release", "net10.0-windows10.0.19041.0", "NiceWP.App.exe"),
                    Path.Combine(current.FullName, ".artifacts", "dist", "NiceWP-0.0.6-win-x64", "NiceWP.App.exe"),
                    Path.Combine(current.FullName, ".artifacts", "dist", "NiceWP-0.0.5-win-x64", "NiceWP.App.exe")
                };

                foreach (var probe in probePaths)
                {
                    if (File.Exists(probe))
                    {
                        return Path.GetFullPath(probe);
                    }
                }

                current = current.Parent;
            }
        }
        catch
        {
            // Ignore traversal errors
        }

        return null;
    }
}
