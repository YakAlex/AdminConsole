using System.Collections.Concurrent;

namespace AdminConsole.Utils;

/// <summary>
/// Мапить довільно довгі рядки (назви серверів, групи тощо) на короткі
/// числові ID для використання в callback_data inline-кнопок.
///
/// ВАЖЛИВО: Telegram обмежує callback_data 1–64 байтами. Пряме вкладання
/// довгого рядка (наприклад "rdp:VeryLongServerName.corporate.domain.local")
/// ризикує перевищити ліміт і викликати виняток при відправці клавіатури.
///
/// Живе виключно в пам'яті процесу — не персистентний. Це нормально:
/// мапінг актуальний лише в межах поточної "сесії" списку серверів
/// (перезапуск бота — новий список кнопок, нові ID).
/// </summary>
public sealed class TelegramCallbackRegistry
{
    private readonly ConcurrentDictionary<int, string> _forward = new();

    /// <summary>
    /// Зворотній індекс для дедуплікації — без нього кожен виклик Register()
    /// з тим самим value створював би новий запис, і за місяці 24/7-роботи
    /// _forward зростав би необмежено (memory leak).
    /// </summary>
    private readonly ConcurrentDictionary<string, int> _reverse = new();

    private int _nextId;

    public int Register(string value)
    {
        // Дедуплікація: якщо це значення вже зареєстроване — повертаємо
        // існуючий ID замість створення нового запису.
        if (_reverse.TryGetValue(value, out int existingId))
            return existingId;

        int candidateId = Interlocked.Increment(ref _nextId);

        // GetOrAdd атомарно вирішує гонку, якщо два потоки одночасно
        // реєструють те саме значення вперше — переможець лише один,
        // "програний" candidateId просто лишається невикористаним номером
        // (нешкідливо — це лише лічильник, не масив).
        int wonId = _reverse.GetOrAdd(value, candidateId);
        if (wonId == candidateId)
            _forward[candidateId] = value;

        return wonId;
    }

    public string? Resolve(int id) => _forward.TryGetValue(id, out var value) ? value : null;
}