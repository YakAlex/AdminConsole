namespace AdminConsole.Core.Models;

/// <summary>Один запис з масиву "BackupChecks" у appsettings.json.</summary>
public sealed class BackupCheckDefinition
{
    public string ServerName { get; init; } = string.Empty;

    /// <summary>Локальний або UNC-шлях до теки з файлами бекапів.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Glob-патерн імені файлу повних бекапів (напр. "*_full_*.bak").</summary>
    public string FullPattern { get; init; } = string.Empty;

    /// <summary>
    /// Glob-патерн імені файлу diff/incremental-бекапів.
    /// Порожній рядок — Diff для цього сервера не перевіряється.
    /// </summary>
    public string DiffPattern { get; init; } = string.Empty;

    public int MaxAgeHoursFull { get; init; } = 26;
    public int MaxAgeHoursDiff { get; init; } = 26;

    /// <summary>
    /// Поріг відхилення поточного розміру від середнього по History
    /// (у відсотках), понад який виставляється SizeWarning.
    /// </summary>
    public int SizeWarningThresholdPct { get; init; } = 30;

    /// <summary>
    /// Мінімальна кількість зразків в History, перш ніж розмір взагалі
    /// оцінюється. До того — перевіряється лише вік, статус Ok.
    /// </summary>
    public int MinSamplesForBaseline { get; init; } = 3;

    /// <summary>Скільки циклів поспіль "сирий" результат має триматись, перш ніж стати підтвердженим (анти-флапінг).</summary>
    public int MinConsecutiveForAlert { get; init; } = 2;
}
