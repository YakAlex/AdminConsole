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

    public UserSettingsService(ILogger<UserSettingsService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Поточні налаштування. При першому зверненні завантажуються з диску.
    /// Якщо файл відсутній або пошкоджений — повертаються дефолтні значення.
    /// </summary>
    public UserSettings Current => _current ??= Load();

    /// <summary>
    /// Зберігає поточні налаштування на диск.
    /// Потокобезпечний — викликається лише з UI thread.
    /// </summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir); // ідемпотентно
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            File.WriteAllText(SettingsPath, json);
            _logger.LogDebug("UserSettings saved to {Path}", SettingsPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save UserSettings to {Path}", SettingsPath);
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