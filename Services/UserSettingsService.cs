using System.IO;
using System.Text.Json;
using AdminConsole.Configuration;
using Microsoft.Extensions.Logging;

namespace AdminConsole.Services;

/// <summary>
/// Читає та зберігає UserSettings у
/// %LocalAppData%\AdminConsole\user_settings.json.
///
/// Реєструється як Singleton у DI.
/// Завантаження — ліниве, при першому зверненні до Current.
/// </summary>
public sealed class UserSettingsService
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AdminConsole");

    private static readonly string SettingsPath = Path.Combine(
        SettingsDir, "user_settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

private readonly ILogger<UserSettingsService> _logger;
    private UserSettings? _current;

    /// <summary>
    /// Єдиний lock для ВСІХ читань/записів UserSettings, що можуть
    /// приходити з більш ніж одного потоку (UI + Telegram polling thread).
    /// </summary>
    private readonly object _lock = new();
    public UserSettingsService(ILogger<UserSettingsService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Поточні налаштування. При першому зверненні завантажуються з диску.
    /// Якщо файл відсутній або пошкоджений — повертаються дефолтні значення.
    /// ОБЕРЕЖНО: для простих одиничних полів (CloseToTray, RdpMonitoringEnabled
    /// тощо), які читаються/пишуться лише з UI thread, пряме звернення
    /// через Current лишається безпечним, як і раніше.
    /// </summary>
    public UserSettings Current => _current ??= Load();

    /// <summary>
    /// Зберігає поточні налаштування на диск. Потокобезпечний — тепер
    /// дійсно, а не лише в коментарі: весь блок під _lock.
    /// </summary>
    public void Save()
    {
        lock (_lock)
        {
            SaveInternalNoLock();
        }
    }

    private void SaveInternalNoLock()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir); // ідемпотентно
            var json = JsonSerializer.Serialize(Current, JsonOptions);

            var tempPath = SettingsPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, SettingsPath, overwrite: true);

            _logger.LogDebug("UserSettings saved to {Path}", SettingsPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save UserSettings to {Path}", SettingsPath);
        }
    }

    // ── Потокобезпечний доступ до Telegram-стану ────────────────────────────

    /// <summary>
    /// Єдина точка входу для БУДЬ-ЯКОЇ зміни Telegram-пов'язаного стану
    /// (AllowedChatIds, Usernames, PrimaryAdminChatId). Мутація і Save()
    /// відбуваються в одному lock — без вікна для гонки чи для конкурентного
    /// File.Move. Викликач передає делегат, що змінює UserSettings напряму.
    /// </summary>
    public void MutateTelegramState(Action<UserSettings> mutate)
    {
        lock (_lock)
        {
            mutate(Current);
            SaveInternalNoLock();
        }
    }

    /// <summary>
    /// Безпечне читання: повертає РЕЗУЛЬТАТ selector-функції, обчислений
    /// під тим самим lock — тобто копію (наприклад .ToList()), а не
    /// пряме посилання на внутрішню колекцію. Використовуйте це замість
    /// прямого читання Current.TelegramAllowedChatIds/TelegramUsernames.
    /// </summary>
    public T ReadTelegramState<T>(Func<UserSettings, T> selector)
    {
        lock (_lock)
        {
            return selector(Current);
        }
    }

    private UserSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                _logger.LogInformation(
                    "UserSettings not found at {Path}, using defaults.", SettingsPath);
                return new UserSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<UserSettings>(json);

            if (settings is null)
            {
                _logger.LogWarning(
                    "UserSettings deserialized as null from {Path}, using defaults.",
                    SettingsPath);
                return new UserSettings();
            }

            _logger.LogDebug("UserSettings loaded from {Path}", SettingsPath);
            return settings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to load UserSettings from {Path}, using defaults.", SettingsPath);
            return new UserSettings();
        }
    }
}