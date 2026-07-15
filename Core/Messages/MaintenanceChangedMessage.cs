namespace AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
public enum MaintenanceAction { Started, Ended }

/// <summary>
/// Публікується MaintenanceService при старті/завершенні вікна
/// (як вручну через UI, так і автоматично по закінченню To).
///
/// Підписники:
///   - UptimeTrackerService  (Started) — закриває "завислі" відкриті інциденти
///   - PingMonitorService    (Ended)   — скидає previousStatus щоб згенерувати
///                                       свіжий алерт якщо сервер все ще Offline
///   - PingResultViewModel   (обидва)  — миттєво оновлює бейдж у UI
/// </summary>
public sealed class MaintenanceChangedMessage
{
    public required MaintenanceAction  Action { get; init; }
    public required MaintenanceWindow  Window { get; init; }
}