namespace AdminConsole.Core.Models;

/// <summary>
/// Один інцидент недоступності сервера.
/// FellAt — момент переходу Online→Offline.
/// RecoveredAt — момент переходу Offline→Online (null = досі offline).
/// Зберігається у JSON на диску через UptimeTrackerService.
/// </summary>
public sealed class DowntimeRecord
{
    public string          ServerName   { get; init; } = string.Empty;
    public string          ServerIp     { get; init; } = string.Empty;
    public string          ServerGroup  { get; init; } = string.Empty;
    public DateTimeOffset  FellAt       { get; init; }
    public DateTimeOffset? RecoveredAt  { get; set;  }

    /// <summary>
    /// Тривалість простою. Якщо ще не відновлено — рахується до поточного моменту.
    /// </summary>
    public TimeSpan Duration => RecoveredAt.HasValue
        ? RecoveredAt.Value - FellAt
        : DateTimeOffset.Now - FellAt;

    public bool IsResolved => RecoveredAt.HasValue;

    /// <summary>Форматований рядок тривалості для UI: "1г 23хв" або "00:05:12".</summary>
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