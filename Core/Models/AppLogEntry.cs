namespace AdminConsole.Core.Models;

public enum LogSeverity
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>
/// A single structured application log entry.
/// Immutable record produced by any service, consumed by
/// FileLoggerService (disk) and LogsViewModel (UI).
/// </summary>
public sealed record AppLogEntry(
    LogSeverity Severity,
    string      Source,
    string      Message,
    DateTimeOffset Timestamp
)
{
    /// <summary>
    /// Максимальна довжина одного лог-повідомлення. Захист від того,
    /// щоб зовнішній некерований вхід (наприклад, довільний текст
    /// Telegram-повідомлення від неавторизованого користувача) не міг
    /// роздути один рядок логу до неконтрольованого розміру.
    /// </summary>
    private const int MaxMessageLength = 2000;

    // Кешуємо при першому зверненні — record immutable, значення ніколи не змінюється.
    // Уникаємо повторного форматування рядка при кожному записі у файл та відображенні в UI.
    //
    // Log Injection fix: Message може містити СИРИЙ, зовнішньо контрольований
    // текст (наприклад, вміст Telegram-повідомлення від будь-кого, хто написав
    // боту — навіть неавторизований). Без санітизації символи \r\n дозволяють
    // підробити вигляд ЦІЛОГО додаткового рядка логу у файлі — атакуючий міг би
    // імітувати справжній системний запис. Тому прибираємо/екрануємо переноси
    // рядків і контрольні символи ДО того, як рядок потрапляє у Formatted.
    public string Formatted { get; } =
        $"[{Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}] [{Severity,-7}] [{Source}] {Sanitize(Message)}";

    private static string Sanitize(string message)
    {
        if (string.IsNullOrEmpty(message)) return message;

        // Замінюємо будь-які переноси рядків на видимий, безпечний маркер —
        // зберігаємо інформацію (не просто видаляємо), але унеможливлюємо
        // підробку окремого рядка логу.
        var sb = new System.Text.StringBuilder(message.Length);
        foreach (char c in message)
        {
            switch (c)
            {
                case '\r': break; // прибираємо повністю — \n нижче вже покриє перенос
                case '\n': sb.Append("⏎"); break;
                default:
                    // Інші керуючі символи (крім звичайних друкованих) теж прибираємо —
                    // захист від інших форм ін'єкції в термінал/файл (напр. escape-послідовності).
                    if (char.IsControl(c)) continue;
                    sb.Append(c);
                    break;
            }
        }

        string result = sb.ToString();
        return result.Length > MaxMessageLength
            ? result[..MaxMessageLength] + "…(обрізано)"
            : result;
    }
}