using AdminConsole.Core.Models;
using System.Windows.Media;

namespace AdminConsole.ViewModels;

/// <summary>
/// Read-only display wrapper for a single ZabbixProblem.
/// Pre-computes frozen brushes at construction time —
/// zero converter overhead in the DataGrid.
/// </summary>
public sealed class ZabbixProblemViewModel
{
    public string         EventId       { get; }
    public string         HostName      { get; }
    public string         Description   { get; }
    public ZabbixSeverity Severity      { get; }
    public string         SeverityLabel { get; }
    public string         StartTime     { get; }
    public string         AgeDisplay    { get; }

    public SolidColorBrush SeverityBackground { get; }
    public SolidColorBrush SeverityForeground { get; }
    public SolidColorBrush RowForeground      { get; }

    public ZabbixProblemViewModel(ZabbixProblem problem)
    {
        EventId     = problem.EventId;
        HostName    = problem.HostName;
        Description = problem.Description;
        Severity    = problem.Severity;
        AgeDisplay  = problem.AgeDisplay;
        StartTime   = problem.StartTime.ToLocalTime().ToString("MM-dd HH:mm");

        SeverityLabel = problem.Severity switch
        {
            ZabbixSeverity.Disaster    => "DISASTER",
            ZabbixSeverity.High        => "HIGH",
            ZabbixSeverity.Average     => "AVERAGE",
            ZabbixSeverity.Warning     => "WARNING",
            ZabbixSeverity.Information => "INFO",
            _                          => "N/A"
        };

        (SeverityBackground, SeverityForeground, RowForeground) = problem.Severity switch
        {
            ZabbixSeverity.Disaster  => (Freeze("#CCE53935"), Freeze("#FFFFFFFF"), Freeze("#FFEF9A9A")),
            ZabbixSeverity.High      => (Freeze("#CCFF5722"), Freeze("#FFFFFFFF"), Freeze("#FFFFCCBC")),
            ZabbixSeverity.Average   => (Freeze("#CCFF9800"), Freeze("#FF000000"), Freeze("#FFFFE0B2")),
            ZabbixSeverity.Warning   => (Freeze("#CCFFD600"), Freeze("#FF000000"), Freeze("#FFFFFF9C")),
            ZabbixSeverity.Information => (Freeze("#CC0097A7"), Freeze("#FFFFFFFF"), Freeze("#FFB2EBF2")),
            _                          => (Freeze("#CC607D8B"), Freeze("#FFFFFFFF"), Freeze("#FFCFD8DC"))
        };
    }

    private static SolidColorBrush Freeze(string hex)
    {
        var b = new SolidColorBrush(
            (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}