namespace AdminConsole.Core.Models;

public enum ZabbixSeverity
{
    NotClassified = 0,
    Information   = 1,
    Warning       = 2,
    Average       = 3,
    High          = 4,
    Disaster      = 5
}

/// <summary>
/// Immutable snapshot of a single Zabbix active problem.
/// Produced by ZabbixPollerService, consumed by ZabbixViewModel.
/// </summary>
public sealed record ZabbixProblem(
    string         EventId,
    string         HostName,
    string         Description,
    ZabbixSeverity Severity,
    DateTimeOffset StartTime,
    string         AgeDisplay
);