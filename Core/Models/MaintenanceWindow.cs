namespace AdminConsole.Core.Models;
using System.Text.Json.Serialization;

/// <summary>
/// Планове вікно обслуговування для сервера або групи серверів.
/// Поки активне (From..To) — поллери не генерують Warning/Error
/// про недоступність цього сервера, а UptimeTracker не рахує це
/// як SLA-інцидент.
///
/// Ключ у MaintenanceService: ServerIp якщо TargetGroup == null,
/// інакше "group:{TargetGroup}". Одне активне вікно на ключ.
/// </summary>
public sealed class MaintenanceWindow
{
    /// <summary>IP конкретного сервера. Null якщо вікно на всю групу.</summary>
    public string? ServerIp { get; init; }

    /// <summary>Назва групи (ServerEntry.Group). Null якщо вікно на один сервер.</summary>
    public string? TargetGroup { get; init; }

    /// <summary>Ім'я для відображення в UI/логах (сервер або назва групи).</summary>
    public required string DisplayName { get; init; }

    public required DateTimeOffset From { get; init; }

    /// <summary>
    /// Момент автоматичного завершення вікна. Null — "без обмеження часу":
    /// вікно лишається активним, поки адміністратор не вимкне його вручну
    /// (EndMaintenanceEarly) — фоновий цикл автозавершення MaintenanceService
    /// такі вікна просто пропускає при перевірці прострочення.
    /// </summary>
    public DateTimeOffset? To { get; init; }

    public string Reason { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public bool IsActiveAt(DateTimeOffset now) => now >= From && (To is null || now <= To);

    /// <summary>Ключ для зберігання/пошуку в MaintenanceService.</summary>
    [JsonIgnore]
    public string Key => TargetGroup is not null
        ? $"group:{TargetGroup}"
        : ServerIp ?? throw new InvalidOperationException(
            "MaintenanceWindow повинен мати або ServerIp, або TargetGroup.");
}