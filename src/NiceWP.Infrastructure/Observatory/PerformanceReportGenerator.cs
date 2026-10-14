using System.Reflection;
using System.Text;

namespace NiceWP.Infrastructure.Observatory;

/// <summary>
/// Formats local performance snapshot and short-term statistics into a clean, human-readable text report.
/// </summary>
public static class PerformanceReportGenerator
{
    public static string GenerateTextReport(RuntimePerformanceStatus status, string? appVersion = null)
    {
        ArgumentNullException.ThrowIfNull(status);

        var snapshot = status.Snapshot;
        var stats = status.Stats;
        var version = appVersion ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.12";

        var sb = new StringBuilder();
        sb.AppendLine("================================================================================");
        sb.AppendLine($"           NiceWP Performance & Runtime Diagnostics Report");
        sb.AppendLine("================================================================================");
        sb.AppendLine($"Application Version : NiceWP {version}");
        sb.AppendLine($"Report Timestamp    : {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC ({DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} Local)");
        sb.AppendLine($"Runtime Host PID    : {snapshot.ProcessId}");
        sb.AppendLine($"Runtime Host Uptime : {FormatTimeSpan(snapshot.Uptime)}");
        sb.AppendLine();

        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine("  1. RESOURCE UTILIZATION (CURRENT & 60-SECOND WINDOW)");
        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine($"Sample Count        : {stats.SampleCount} samples (~{stats.SampleCount}s history)");
        sb.AppendLine($"CPU Usage (Current) : {FormatPercent(stats.CurrentCpuPercent)}");
        sb.AppendLine($"CPU Usage (Average) : {FormatPercent(stats.AverageCpuPercent)}");
        sb.AppendLine($"CPU Usage (Peak)    : {FormatPercent(stats.PeakCpuPercent)}");
        sb.AppendLine();
        sb.AppendLine($"Working Set (Curr)  : {FormatBytes(stats.CurrentWorkingSetBytes)}");
        sb.AppendLine($"Working Set (Avg)   : {FormatBytes(stats.AverageWorkingSetBytes)}");
        sb.AppendLine($"Working Set (Peak)  : {FormatBytes(stats.PeakWorkingSetBytes)}");
        sb.AppendLine();
        sb.AppendLine($"Private Memory (Cur): {FormatBytes(stats.CurrentPrivateMemoryBytes)}");
        sb.AppendLine($"Private Memory (Avg): {FormatBytes(stats.AveragePrivateMemoryBytes)}");
        sb.AppendLine($"Private Memory (Pk) : {FormatBytes(stats.PeakPrivateMemoryBytes)}");
        sb.AppendLine();
        sb.AppendLine($"Active Threads      : {stats.CurrentThreadCount} (Peak: {stats.PeakThreadCount})");
        sb.AppendLine($"Allocated Handles   : {stats.CurrentHandleCount} (Peak: {stats.PeakHandleCount})");
        sb.AppendLine();

        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine("  2. WALLPAPER & RENDERING STATE");
        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine($"Wallpaper Type      : {snapshot.WallpaperType}");
        sb.AppendLine($"Playback State      : {snapshot.WallpaperState}");
        sb.AppendLine($"Active File Path    : {snapshot.ActiveWallpaperPath ?? "(None)"}");
        sb.AppendLine($"Renderer HWND Alive : {(snapshot.RendererWindowAlive ? "Yes (HWND valid)" : "No")}");
        sb.AppendLine($"Desktop Host Attach : {(snapshot.DesktopHostConnected ? "Connected (WorkerW valid)" : "Disconnected")}");
        sb.AppendLine();

        if (snapshot.IsImageActive)
        {
            sb.AppendLine("Image Details:");
            sb.AppendLine($"  Source Dimensions : {FormatDimensions(snapshot.SourceWidth, snapshot.SourceHeight)}");
            sb.AppendLine($"  Decoded Dimensions: {FormatDimensions(snapshot.DecodeWidth, snapshot.DecodeHeight)}");
            sb.AppendLine($"  Decoded Bitmap Mem: {FormatBytes(snapshot.ApproximateDecodedMemoryBytes)}");
            sb.AppendLine();
        }
        else if (snapshot.IsVideoActive)
        {
            sb.AppendLine("Video Details:");
            sb.AppendLine($"  Video Dimensions  : {FormatDimensions(snapshot.VideoWidth, snapshot.VideoHeight)}");
            sb.AppendLine($"  Playback Status   : {snapshot.PlaybackStatus ?? "Unknown"}");
            sb.AppendLine($"  Playback Position : {FormatTimeSpan(snapshot.PlaybackPosition)} / {FormatTimeSpan(snapshot.PlaybackDuration)}");
            sb.AppendLine();
        }

        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine("  3. EFFICIENCY & POLICIES");
        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine($"Active Pause Reasons: {snapshot.PauseReasons}");
        sb.AppendLine($"Fullscreen Policy   : {(snapshot.FullscreenPauseActive ? "Active (Paused due to foreground window)" : "Inactive")}");
        sb.AppendLine($"Power Policy        : {(snapshot.PowerPauseActive ? "Active (Paused due to battery saver/DC)" : "Inactive")}");
        sb.AppendLine($"Manual Pause        : {(snapshot.ManualPauseActive ? "Active (User paused)" : "Inactive")}");
        sb.AppendLine();

        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine("  4. GPU METRICS");
        sb.AppendLine("--------------------------------------------------------------------------------");
        if (snapshot.GpuPercent.HasValue)
        {
            sb.AppendLine($"GPU Utilization     : {snapshot.GpuPercent.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}%");
        }
        else
        {
            sb.AppendLine("GPU Utilization     : Unavailable");
        }
        sb.AppendLine($"GPU Dedicated Memory: {FormatBytes(snapshot.GpuMemoryBytes)}");
        if (!string.IsNullOrWhiteSpace(snapshot.GpuStatusMessage))
        {
            sb.AppendLine($"GPU Status Note     : {snapshot.GpuStatusMessage}");
        }
        sb.AppendLine();

        sb.AppendLine("================================================================================");
        sb.AppendLine("  DATA TRANSPARENCY & PRIVACY NOTICE");
        sb.AppendLine("================================================================================");
        sb.AppendLine("  * Metrics are measured strictly locally on this machine.");
        sb.AppendLine("  * NiceWP does NOT transmit performance telemetry to any cloud service or network.");
        sb.AppendLine("  * Zero external telemetry SDKs or analytics trackers are bundled.");
        sb.AppendLine("================================================================================");

        return sb.ToString();
    }

    private static string FormatBytes(long? bytes)
    {
        if (!bytes.HasValue || bytes.Value < 0) return "Unavailable";
        var b = bytes.Value;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (b < 1024) return $"{b} B";
        if (b < 1024 * 1024) return $"{(b / 1024.0).ToString("F2", inv)} KB ({b.ToString("N0", inv)} bytes)";
        if (b < 1024L * 1024 * 1024) return $"{(b / (1024.0 * 1024)).ToString("F2", inv)} MB ({b.ToString("N0", inv)} bytes)";
        return $"{(b / (1024.0 * 1024 * 1024)).ToString("F2", inv)} GB ({b.ToString("N0", inv)} bytes)";
    }

    private static string FormatPercent(double? percent) =>
        percent.HasValue ? $"{percent.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}%" : "Unavailable";

    private static string FormatTimeSpan(TimeSpan? ts) =>
        ts.HasValue ? $"{(int)ts.Value.TotalHours:D2}:{ts.Value.Minutes:D2}:{ts.Value.Seconds:D2}" : "Unavailable";

    private static string FormatDimensions(uint? w, uint? h) =>
        w.HasValue && h.HasValue ? $"{w.Value} x {h.Value}" : "Unavailable";
}
