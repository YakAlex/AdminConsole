using AdminConsole.Core.Models;

namespace AdminConsole.ViewModels;

/// <summary>
/// Thin display wrapper around EventLogEntry for DataGrid row binding.
/// Read-only — no ObservableObject needed.
/// </summary>
public sealed class EventLogEntryViewModel
{
    public EventSeverity Severity      { get; }
    public string        SeverityLabel { get; }
    public string        Source        { get; }
    public string        ShortMessage  { get; }
    public string        FullMessage   { get; }
    public string        EventId       { get; }
    public string        Time          { get; }

    public EventLogEntryViewModel(EventLogEntry entry)
    {
        Severity      = entry.Severity;
        SeverityLabel = entry.Severity.ToString().ToUpperInvariant();
        Source        = entry.Source;
        ShortMessage  = entry.Message.Length > 120
            ? string.Concat(entry.Message.AsSpan(0, 120), "…")
            : entry.Message;
        FullMessage   = entry.Message;
        EventId       = entry.EventId;
        Time          = entry.TimeGenerated.ToLocalTime().ToString("MM-dd HH:mm:ss");
    }
}