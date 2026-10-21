using System.Diagnostics;

namespace NiceWP.Infrastructure.Adaptive;

/// <summary>
/// Simple descriptor for a currently active application.
/// Privacy-first: strictly holds the executable process name and product name without capturing window titles.
/// </summary>
public sealed record RunningApplicationInfo(string ProcessName, string DisplayName);

/// <summary>
/// Discovers currently running top-level applications so the user can easily select them when creating App Rules.
/// </summary>
public static class RunningApplicationPickerService
{
    private static readonly HashSet<string> ExcludedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer",
        "dwm",
        "taskmgr",
        "System",
        "Idle",
        "Registry",
        "smss",
        "csrss",
        "wininit",
        "services",
        "lsass",
        "svchost",
        "fontdrvhost",
        "NiceWP.App",
        "NiceWP.Runtime.Host"
    };

    /// <summary>
    /// Enumerates currently running interactive applications with a valid window handle.
    /// Strictly gathers executable process name and company/product label, never capturing window titles or command lines.
    /// </summary>
    public static IReadOnlyList<RunningApplicationInfo> GetRunningApplications()
    {
        var result = new List<RunningApplicationInfo>();
        var seenProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return Array.Empty<RunningApplicationInfo>();
        }

        foreach (var process in processes)
        {
            try
            {
                // Only consider processes that own a non-zero main window handle (desktop interactive apps)
                if (process.MainWindowHandle == 0)
                {
                    continue;
                }

                var procName = process.ProcessName;
                if (string.IsNullOrWhiteSpace(procName) || ExcludedProcesses.Contains(procName))
                {
                    continue;
                }

                var normalizedName = AppRuleNormalizer.NormalizeProcessName(procName);
                if (string.IsNullOrEmpty(normalizedName) || !seenProcesses.Add(normalizedName))
                {
                    continue;
                }

                var displayName = ResolveFriendlyName(process, normalizedName);
                result.Add(new RunningApplicationInfo(normalizedName, displayName));
            }
            catch
            {
                // Process may have exited or access denied
            }
            finally
            {
                try { process.Dispose(); } catch { }
            }
        }

        return result.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string ResolveFriendlyName(Process process, string fallbackName)
    {
        try
        {
            var desc = process.MainModule?.FileVersionInfo?.FileDescription;
            if (!string.IsNullOrWhiteSpace(desc))
            {
                return desc.Trim();
            }
        }
        catch
        {
            // Access denied or 32/64 bit mismatch on MainModule is expected for some system apps
        }

        // Fallback to capitalized process base name
        var baseName = fallbackName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? fallbackName[..^4]
            : fallbackName;

        return char.ToUpperInvariant(baseName[0]) + baseName[1..];
    }
}
