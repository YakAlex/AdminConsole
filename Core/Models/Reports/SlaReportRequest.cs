namespace AdminConsole.Core.Models.Reports;

public sealed class SlaReportRequest
{
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To   { get; init; }
    public string? GroupFilter  { get; init; }   // null = усі групи
    public string? ServerFilter { get; init; }   // null = усі сервери (підрядок імені)
}