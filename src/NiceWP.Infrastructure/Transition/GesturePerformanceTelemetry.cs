namespace NiceWP.Infrastructure.Transition;

/// <summary>
/// Telemetry metrics tracking real-time input-to-render latency, render execution time,
/// frame pacing, and event coalescing efficiency during corner page-turn gestures.
/// </summary>
public sealed class GesturePerformanceTelemetry
{
    public double GestureInputLatencyMs { get; set; }
    public double GestureRenderMs { get; set; }
    public double GesturePresentMs { get; set; }
    public int DroppedPointerEvents { get; set; }
    public int TotalPointerEvents { get; set; }
    public int RenderedFrames { get; set; }
    public double AverageFPS { get; set; }
    public double TotalGestureDurationMs { get; set; }

    public void Reset()
    {
        GestureInputLatencyMs = 0;
        GestureRenderMs = 0;
        GesturePresentMs = 0;
        DroppedPointerEvents = 0;
        TotalPointerEvents = 0;
        RenderedFrames = 0;
        AverageFPS = 0;
        TotalGestureDurationMs = 0;
    }

    public void RecordFrame(double inputLatencyMs, double renderMs, double presentMs)
    {
        GestureInputLatencyMs = inputLatencyMs;
        GestureRenderMs = renderMs;
        GesturePresentMs = presentMs;
        RenderedFrames++;
    }
}
