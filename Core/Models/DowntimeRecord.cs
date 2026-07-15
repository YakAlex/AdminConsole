namespace AdminConsole.Core.Models;
using System.Text.Json.Serialization;

public sealed class DowntimeRecord
{
    public string          ServerName   { get; init; } = string.Empty;
    public string          ServerIp     { get; init; } = string.Empty;
    public string          ServerGroup  { get; init; } = string.Empty;
    public DateTimeOffset  FellAt       { get; init; }
    public DateTimeOffset? RecoveredAt  { get; set;  }

    /// <summary>
    /// true — інцидент закрито не тому що сервер реально відновився,
    /// а тому що адміністратор увімкнув Maintenance Mode поки інцидент
    /// був відкритий. Дозволяє відрізняти "справжні" даунтайми від
    /// перерваних вручну при розрахунку SLA-звітів.
    /// </summary>
    public bool ClosedByMaintenance { get; set; }

    [JsonIgnore]
    public TimeSpan Duration => RecoveredAt.HasValue
        ? RecoveredAt.Value - FellAt
        : DateTimeOffset.Now - FellAt;

    [JsonIgnore]
    public bool IsResolved => RecoveredAt.HasValue;

    [JsonIgnore]
    public string DurationDisplay
    {
        get
        {
            var d = Duration;
            string baseText = d.TotalHours >= 1
                ? $"{(int)d.TotalHours}г {d.Minutes:D2}хв"
                : d.TotalMinutes >= 1
                    ? $"{(int)d.TotalMinutes}хв {d.Seconds:D2}с"
                    : $"{d.Seconds}с";

            return ClosedByMaintenance ? $"{baseText} (maintenance)" : baseText;
        }
    }
}