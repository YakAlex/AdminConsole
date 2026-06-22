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
    // Кешуємо при першому зверненні — record immutable, значення ніколи не змінюється.
    // Уникаємо повторного форматування рядка при кожному записі у файл та відображенні в UI.
    public string Formatted { get; } =
        $"[{Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}] [{Severity,-7}] [{Source}] {Message}";
}