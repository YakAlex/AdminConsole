namespace AdminConsole.Core.Models;

/// <summary>
/// Рантайм-стан однієї перевірки (сервер + Full/Diff). Персистується
/// у logs/backups.json (atomic write, той самий патерн, що й
/// DowntimeRecord/MaintenanceWindow — temp-файл + File.Move(overwrite: true)).
/// </summary>
public sealed class BackupCheckState
{
    public string Name { get; init; } = string.Empty;
    
    public string Host { get; set; } = string.Empty;
    public BackupKind Kind       { get; init; }

    /// <summary>
    /// Поточний, ПІДТВЕРДЖЕНИЙ (пост-анти-флапінг) стан — саме він
    /// показується в UI і провокує Telegram-алерт при переході
    /// в Stale/Missing.
    /// </summary>
    public BackupOutcome Outcome { get; set; } = BackupOutcome.Unknown;

    /// <summary>Коли востаннє був підтверджений НЕ-Unknown результат.</summary>
    public DateTimeOffset? LastConfirmedAt { get; set; }

    /// <summary>Який саме був той останній підтверджений НЕ-Unknown результат.</summary>
    public BackupOutcome? LastConfirmedOutcome { get; set; }

    /// <summary>Скільки циклів поспіль перевірка не змогла достукатись (Stage A).</summary>
    public int ConsecutiveUnknownCount { get; set; }

    /// <summary>
    /// Анти-флапінг лічильник: скільки циклів поспіль "сирий" результат
    /// Stage B відрізняється від поточного підтвердженого Outcome.
    /// Перехід у Outcome стається лише після MinConsecutiveForAlert
    /// однакових "сирих" результатів поспіль.
    /// </summary>
    public int ConsecutiveBadCount { get; set; }
    
    /// <summary>
    /// Останній побачений "сирий" (до анти-флапінгу) результат — потрібен,
    /// щоб рахувати саме ОДНАКОВІ результати поспіль, а не будь-яку зміну.
    /// Null одразу після кожного підтвердженого переходу (нова серія
    /// рахується з чистого аркуша).
    /// </summary>
    public BackupOutcome? LastRawOutcome { get; set; }

    /// <summary>Текст останньої помилки Stage A (для показу в UI/логах). Null, якщо перевірка не падала.</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Rolling-вікно останніх зразків розміру — лише з циклів, де
    /// Stage A пройшов успішно. Обмежується сервісом до фіксованої
    /// довжини (MaxHistorySamples, напр. 14) при кожному додаванні.
    /// </summary>
    public List<BackupSample> History { get; init; } = new();
}
