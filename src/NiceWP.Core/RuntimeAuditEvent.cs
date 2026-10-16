namespace NiceWP.Core;

/// <summary>
/// Lightweight, in-memory audit record for a runtime lifecycle or policy event.
/// </summary>
public readonly record struct RuntimeAuditEvent(
    DateTimeOffset TimestampUtc,
    string EventType,
    string Message)
{
    public override string ToString() =>
        $"[{TimestampUtc:HH:mm:ss}] {EventType}: {Message}";
}
