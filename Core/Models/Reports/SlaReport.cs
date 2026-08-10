namespace AdminConsole.Core.Models.Reports;

public sealed class SlaReport
{
    public required DateTimeOffset GeneratedAt          { get; init; }
    public required DateTimeOffset From                 { get; init; }
    public required DateTimeOffset To                   { get; init; }
    public required double?        OverallUptimePercent { get; init; } // null якщо 0 серверів
    public required IReadOnlyList<ServerSlaEntry> Servers             { get; init; }
    public required IReadOnlyList<IncidentDetail> MaintenanceAppendix { get; init; }
}