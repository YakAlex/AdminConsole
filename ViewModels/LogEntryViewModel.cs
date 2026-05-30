using AdminConsole.Core.Models;
using System.Windows.Media;

namespace AdminConsole.ViewModels;

/// <summary>
/// Read-only display wrapper for a single AppLogEntry.
/// Calculates display colours once on construction so the
/// DataGrid never re-runs converter logic per scroll.
/// </summary>
public sealed class LogEntryViewModel
{
    public LogSeverity Severity    { get; }
    public string      TimeShort   { get; }
    public string      TimeFull    { get; }
    public string      Source      { get; }
    public string      Message     { get; }
    public string      SeverityTag { get; }

    // Pre-computed brushes — frozen for zero-allocation rendering.
    public SolidColorBrush TagBackground { get; }
    public SolidColorBrush TagForeground { get; }
    public SolidColorBrush RowForeground { get; }

    public LogEntryViewModel(AppLogEntry entry)
    {
        Severity    = entry.Severity;
        TimeShort   = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss");
        TimeFull    = entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        Source      = entry.Source;
        Message     = entry.Message;
        SeverityTag = entry.Severity.ToString().ToUpperInvariant();

        (TagBackground, TagForeground, RowForeground) = entry.Severity switch
        {
            LogSeverity.Error   => (Freeze("#CCF44336"), Freeze("#FFFFFFFF"), Freeze("#FFEF9A9A")),
            LogSeverity.Warning => (Freeze("#CCFF6F00"), Freeze("#FFFFFFFF"), Freeze("#FFFFCC80")),
            LogSeverity.Success => (Freeze("#CC4CAF50"), Freeze("#FFFFFFFF"), Freeze("#FFA5D6A7")),
            _                   => (Freeze("#CC546E7A"), Freeze("#FFFFFFFF"), Freeze("#FFCFD8DC")),
        };
    }

    private static SolidColorBrush Freeze(string hex)
    {
        var brush = new SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter
                .ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}