namespace AdminConsole.Core.Models;

/// <summary>
/// Immutable snapshot of local machine resource usage at a point in time.
/// Produced by ResourceMonitorService, consumed by ResourceMonitorViewModel.
/// </summary>
public sealed record ResourceSnapshot(
    double CpuPercent,
    double RamUsedGb,
    double RamTotalGb,
    double RamPercent,
    DateTimeOffset Timestamp
);