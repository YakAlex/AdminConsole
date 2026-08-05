namespace AdminConsole.Core.Models.Reports;

public sealed class IncidentDetail
{
    public required string ServerName  { get; init; }
    public required string ServerGroup { get; init; }

    public required DateTimeOffset  FellAt              { get; init; }
    public required DateTimeOffset? RecoveredAt         { get; init; }
    public required TimeSpan        EffectiveDuration   { get; init; }
    public required bool            IsOngoing            { get; init; }
    public required bool            ClosedByMaintenance  { get; init; }
}