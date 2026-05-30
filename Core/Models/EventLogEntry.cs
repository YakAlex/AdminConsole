namespace AdminConsole.Core.Models;

public enum EventSeverity
{
    Information,
    Warning,
    Error,
    Critical
}

/// <summary>
/// Represents a single Windows Event Log entry.
/// Immutable record — produced by EventLogService.
/// </summary>
public sealed record EventLogEntry(
    EventSeverity Severity,
    string        Source,
    string        Message,
    string        EventId,
    DateTimeOffset TimeGenerated
);