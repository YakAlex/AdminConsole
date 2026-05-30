namespace AdminConsole.Core.Models;

public enum PingStatus
{
    Unknown,
    Online,
    Offline,
    Checking
}

/// <summary>
/// Immutable snapshot of a single ping result.
/// Produced by PingMonitorService, consumed by PingDashboardViewModel.
/// This is a pure data record — no UI concerns whatsoever.
/// </summary>
public sealed record PingResult(
    string Name,
    string IP,
    string Group,
    PingStatus Status,
    long? LatencyMs,
    DateTimeOffset LastChecked
);