namespace AdminConsole.Core.Models;
using System.Text.Json.Serialization;

public sealed class DowntimeRecord
{
    public string          ServerName   { get; init; } = string.Empty;
    public string          ServerIp     { get; init; } = string.Empty;
    public string          ServerGroup  { get; init; } = string.Empty;
    public DateTimeOffset  FellAt       { get; init; }
    public DateTimeOffset? RecoveredAt  { get; set;  }

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
            if (d.TotalHours >= 1)
                return $"{(int)d.TotalHours}г {d.Minutes:D2}хв";
            if (d.TotalMinutes >= 1)
                return $"{(int)d.TotalMinutes}хв {d.Seconds:D2}с";
            return $"{d.Seconds}с";
        }
    }
}