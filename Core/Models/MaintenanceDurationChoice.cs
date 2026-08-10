namespace AdminConsole.Core.Models;

/// <summary>
/// Результат вибору тривалості в діалозі старту Maintenance.
/// Duration == null означає "без обмеження часу" (до ручного вимкнення).
/// Сам факт null-результату методу діалогу (не цього типу, а Task-результату)
/// означає "користувач скасував" — не плутати з Duration == null.
/// </summary>
public sealed class MaintenanceDurationChoice
{
    public TimeSpan? Duration { get; init; }

    /// <summary>
    /// Вільний коментар, введений адміністратором у діалозі (необов'язковий).
    /// Порожній/не введений — виклик використовує дефолтну причину
    /// "Планове обслуговування" (див. PingResultViewModel.ToggleMaintenance).
    /// </summary>
    public string? Comment { get; init; }
}