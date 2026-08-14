namespace AdminConsole.Core.Models;

/// <summary>
/// Результат ОДНІЄЇ спроби перевірки (один цикл, один сервер, один Kind).
/// "Сирий" — без анти-флапінгу й без запису в LastConfirmed*; це вже
/// відповідальність BackupMonitorService (Фаза 2).
/// </summary>
public sealed class BackupCheckResult
{
    public required BackupOutcome Outcome { get; init; }

    /// <summary>Заповнено, якщо файл за патерном знайдено (Ok / SizeWarning / Stale). Null для Missing і Unknown.</summary>
    public BackupSample? Sample { get; init; }

    /// <summary>Заповнено лише для Outcome == Unknown — текст винятку Stage A.</summary>
    public string? ErrorMessage { get; init; }

    public static BackupCheckResult Unknown(string error) =>
        new() { Outcome = BackupOutcome.Unknown, ErrorMessage = error };

    public static BackupCheckResult Missing() =>
        new() { Outcome = BackupOutcome.Missing };

    public static BackupCheckResult Stale(BackupSample sample) =>
        new() { Outcome = BackupOutcome.Stale, Sample = sample };

    public static BackupCheckResult Ok(BackupSample sample) =>
        new() { Outcome = BackupOutcome.Ok, Sample = sample };

    public static BackupCheckResult SizeWarning(BackupSample sample) =>
        new() { Outcome = BackupOutcome.SizeWarning, Sample = sample };
}
