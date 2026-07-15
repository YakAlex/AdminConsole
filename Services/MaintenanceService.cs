using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace AdminConsole.Services;

/// <summary>
/// Керує вікнами планового обслуговування (Maintenance Windows).
///
/// Гібридна модель Pull + Push:
///   - Pull: поллери (Ping, RDP) синхронно викликають IsUnderMaintenance
///     перед відправкою Warning/Error повідомлень — без затримки messenger.
///   - Push: StartMaintenance / автозавершення в ExecuteAsync шлють
///     MaintenanceChangedMessage — UptimeTracker закриває інциденти,
///     PingMonitor перегенеровує алерти, UI оновлює бейджі миттєво.
///
/// Сховище — ConcurrentDictionary, бо читається одночасно з кількох
/// фонових потоків (Ping/RDP поллери на кожному циклі) і пишеться
/// з UI-потоку (створення/завершення вікна) та з власного фонового
/// циклу автозавершення.
/// </summary>
public sealed class MaintenanceService : BackgroundService
{
    private readonly IMessenger                  _messenger;
    private readonly ILogger<MaintenanceService> _logger;

    private readonly ConcurrentDictionary<string, MaintenanceWindow> _windows = new();

    private static readonly string FilePath = Path.Combine(
        AppContext.BaseDirectory, "logs", "maintenance.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _saveLock = new();

    private const string LogSource            = "Maintenance";
    private const int    CheckIntervalSeconds = 30;

    public MaintenanceService(
        IMessenger                  messenger,
        ILogger<MaintenanceService> logger)
    {
        _messenger = messenger;
        _logger    = logger;
        LoadFromDisk();
    }

    // ── Pull API — викликається з фонових поллерів ─────────────────────────

    public bool IsUnderMaintenance(string serverIp, string group)
    {
        var now = DateTimeOffset.Now;

        if (_windows.TryGetValue(serverIp, out var w) && w.IsActiveAt(now))
            return true;

        if (!string.IsNullOrEmpty(group) &&
            _windows.TryGetValue($"group:{group}", out var gw) && gw.IsActiveAt(now))
            return true;

        return false;
    }

    /// <summary>Повертає активне вікно (для UI — показати Reason/To у тултипі).</summary>
    public MaintenanceWindow? GetActiveWindow(string serverIp, string group)
    {
        var now = DateTimeOffset.Now;

        if (_windows.TryGetValue(serverIp, out var w) && w.IsActiveAt(now))
            return w;

        if (!string.IsNullOrEmpty(group) &&
            _windows.TryGetValue($"group:{group}", out var gw) && gw.IsActiveAt(now))
            return gw;

        return null;
    }

    // ── Push API — викликається з UI ────────────────────────────────────────

    public void StartMaintenance(MaintenanceWindow window)
    {
        _windows[window.Key] = window;
        SaveToDisk();

        _logger.LogInformation(
            "Maintenance розпочато: {Key}, до {To}", window.Key, window.To);
        _messenger.Send(AppLogEntryMessage.Info(LogSource,
            $"Maintenance розпочато для {window.DisplayName}: " +
            $"{(string.IsNullOrWhiteSpace(window.Reason) ? "без причини" : window.Reason)} " +
            $"(до {window.To.ToLocalTime():dd.MM HH:mm})."));

        _messenger.Send(new MaintenanceChangedMessage
        {
            Action = MaintenanceAction.Started,
            Window = window
        });
    }

    /// <summary>Дострокове завершення вручну (адмін відновив сервер раніше графіка).</summary>
    public void EndMaintenanceEarly(string key)
    {
        if (!_windows.TryRemove(key, out var window))
            return;

        SaveToDisk();

        _messenger.Send(AppLogEntryMessage.Info(LogSource,
            $"Maintenance для {window.DisplayName} завершено вручну."));

        _messenger.Send(new MaintenanceChangedMessage
        {
            Action = MaintenanceAction.Ended,
            Window = window
        });
    }

    // ── Фонове автозавершення прострочених вікон ────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MaintenanceService started. {Count} активних вікон завантажено.",
            _windows.Count);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(CheckIntervalSeconds), stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }

            var now = DateTimeOffset.Now;
            var expired = _windows.Where(kv => kv.Value.To < now).ToList();
            if (expired.Count == 0) continue;

            foreach (var (key, window) in expired)
            {
                if (!_windows.TryRemove(key, out _)) continue;

                _messenger.Send(AppLogEntryMessage.Info(LogSource,
                    $"Maintenance для {window.DisplayName} завершено (час вийшов)."));

                _messenger.Send(new MaintenanceChangedMessage
                {
                    Action = MaintenanceAction.Ended,
                    Window = window
                });
            }

            SaveToDisk();
        }
    }

    // ── Persistence (temp+rename, за зразком UptimeTrackerService) ─────────

    private void SaveToDisk()
    {
        try
        {
            var json = JsonSerializer.Serialize(_windows.Values.ToList(), JsonOptions);

            lock (_saveLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var tempPath = FilePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, FilePath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MaintenanceService: помилка збереження на диск.");
        }
    }

    private void LoadFromDisk()
    {
        if (!File.Exists(FilePath)) return;

        try
        {
            var json    = File.ReadAllText(FilePath);
            var windows = JsonSerializer.Deserialize<List<MaintenanceWindow>>(json, JsonOptions);
            if (windows is null) return;

            var now = DateTimeOffset.Now;
            foreach (var w in windows)
            {
                // Не відновлюємо вже прострочені вікна (застосунок міг
                // не працювати довше, ніж тривало вікно) — інакше вони
                // одразу потраплять у перший цикл автозавершення,
                // що не страшно, але для чистоти фільтруємо тут.
                if (w.To >= now)
                    _windows[w.Key] = w;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MaintenanceService: помилка читання {Path}", FilePath);
        }
    }
}