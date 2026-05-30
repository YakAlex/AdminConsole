namespace AdminConsole.Core.Models;

public enum LogSeverity
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>
/// A single structured application log entry.
/// Immutable record produced by any service, consumed by
/// FileLoggerService (disk) and LogsViewModel (UI).
/// </summary>
public sealed record AppLogEntry(
    LogSeverity Severity,
    string      Source,
    string      Message,
    DateTimeOffset Timestamp
)
{
    /// <summary>Pre-formatted line written to the log file and displayed in the UI.</summary>
    public string Formatted =>
        $"[{Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}] [{Severity,-7}] [{Source}] {Message}";
}