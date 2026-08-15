using System.Collections.Concurrent;
using System.Text.Json;
using System.IO;
namespace AdminConsole.Utils;

/// <summary>
/// Спільний atomic JSON persistence helper — виносить дубльовану логіку
/// "temp-файл + atomic rename", яка раніше була окремо скопійована
/// в MaintenanceService, BackupMonitorService і UptimeTrackerService.
///
/// Хелпер відповідає ЛИШЕ за механіку файлового I/O (серіалізація,
/// atomic-запис, читання, лок на конкретний шлях). Доменна логіка
/// (дедуп, групування по місяцях, фільтрація прострочених записів
/// тощо) лишається в кожному сервісі — це свідома межа, щоб хелпер
/// не перетворився на плутанину з умовами під кожен окремий кейс.
/// </summary>
public static class JsonFileStore
{
    /// <summary>
    /// Дефолтні опції серіалізації — WriteIndented, без додаткових конвертерів.
    /// Сервіси з особливими потребами (напр. BackupMonitorService з
    /// JsonStringEnumConverter) передають власні options.
    /// </summary>
    public static readonly JsonSerializerOptions DefaultOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Локи per-шлях, а не один глобальний лок на всі файли — різні файли
    /// (напр. uptime-2026-01.json і uptime-2026-02.json) пишуться повністю
    /// незалежно один від одного.
    ///
    /// ВАЖЛИВО: Windows не розрізняє регістр у шляхах (C:\Logs\file.json
    /// і c:\logs\FILE.json — той самий файл), тому словник ключується
    /// з OrdinalIgnoreCase — інакше можна отримати два різні лок-об'єкти
    /// на один і той самий фізичний файл і втратити atomic-гарантію.
    /// </summary>
    private static readonly ConcurrentDictionary<string, object> _locks =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Нормалізує шлях перед використанням як ключ локу — Path.GetFullPath
    /// гарантує, що відносний ("logs/backups.json") і абсолютний
    /// ("C:/App/logs/backups.json") шлях до того самого файлу розпізнаються
    /// як один ключ, а не два різні.
    /// </summary>
    private static object GetLock(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return _locks.GetOrAdd(fullPath, static _ => new object());
    }

    /// <summary>
    /// Атомарно серіалізує <paramref name="data"/> в JSON і записує на
    /// <paramref name="path"/> через temp-файл + File.Move(overwrite: true) —
    /// той самий патерн, що раніше був скопійований в кожному сервісі окремо.
    /// Створює батьківську директорію, якщо її ще немає.
    /// </summary>
    public static void SaveAtomic<T>(
        string path,
        T data,
        JsonSerializerOptions? options = null)
    {
        var json = JsonSerializer.Serialize(data, options ?? DefaultOptions);

        lock (GetLock(path))
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
        }
    }

    /// <summary>
    /// Читає і десеріалізує один файл. Повертає default(T) (зазвичай null),
    /// якщо файлу немає — сумісно з поточним патерном "if (!File.Exists) return"
    /// у кожному з трьох сервісів. Якщо файл існує, але зіпсований —
    /// проброшує виняток ДАЛІ: хелпер не вирішує, як на це реагувати,
    /// кожен викликач і далі сам вирішує через власний try/catch + логер,
    /// як зараз.
    /// </summary>
    public static T? TryLoad<T>(
        string path,
        JsonSerializerOptions? options = null)
    {
        if (!File.Exists(path))
            return default;

        // Читання не потребує лока на запис — File.WriteAllText+Move пише
        // в .tmp і лише в кінці атомарно підміняє основний файл, тому
        // паралельний File.ReadAllText завжди бачить або повністю старий,
        // або повністю новий вміст, ніколи "напівзаписаний".
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, options ?? DefaultOptions);
    }

    /// <summary>
    /// Читає й зливає в один список усі файли за <paramref name="searchPattern"/>
    /// у вказаній директорії (напр. "uptime-*.json") — під кейс
    /// UptimeTrackerService.LoadFromDisk(), яка читає ВЕСЬ архів місячних
    /// файлів одразу, а не лише поточний місяць.
    ///
    /// Кожен файл повинен містити List&lt;T&gt;. Якщо один файл зіпсований —
    /// пропускається (виклик onFileError, якщо переданий), решта файлів
    /// все одно завантажуються — та сама стійкість, що й у поточній
    /// реалізації UptimeTrackerService.
    /// </summary>
    public static List<T> LoadAllMatching<T>(
        string directory,
        string searchPattern,
        JsonSerializerOptions? options = null,
        Action<string, Exception>? onFileError = null)
    {
        var result = new List<T>();

        if (!Directory.Exists(directory))
            return result;

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, searchPattern);
        }
        catch (Exception ex)
        {
            onFileError?.Invoke(directory, ex);
            return result;
        }

        foreach (var file in files)
        {
            try
            {
                var json    = File.ReadAllText(file);
                var records = JsonSerializer.Deserialize<List<T>>(json, options ?? DefaultOptions);
                if (records is not null)
                    result.AddRange(records);
            }
            catch (Exception ex)
            {
                onFileError?.Invoke(file, ex);
            }
        }

        return result;
    }

    /// <summary>
    /// Видаляє файл, якщо він існує — під per-шляховим локом, тим самим,
    /// що й SaveAtomic, щоб не конкурувати з паралельним записом у той
    /// самий шлях (напр. UptimeTrackerService видаляє файл місяця,
    /// що спорожнів, замість переписувати його порожнім масивом).
    /// </summary>
    public static void DeleteIfExists(string path)
    {
        lock (GetLock(path))
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}