using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using AdminConsole.Configuration;
using Microsoft.Extensions.Options;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Text.Json;

namespace AdminConsole.Services;

/// <summary>
/// Відслідковує переходи Online↔Offline для кожного сервера.
/// Підписується на PingBatchResultMessage.
/// Зберігає інциденти у logs/uptime-YYYY-MM.json з ротацією по місяцях.
/// Публікує UptimeUpdatedMessage при кожній зміні.
/// </summary>
public sealed class UptimeTrackerService
    : BackgroundService,
        IRecipient<PingBatchResultMessage>,
        IRecipient<MaintenanceChangedMessage>,
        IDisposable
{
    private readonly IMessenger                   _messenger;
    private readonly ILogger<UptimeTrackerService> _logger;
    private readonly MaintenanceService _maintenance;
    private readonly MonitoringSettings _settings;

    /// Поточний статус кожного IP (для визначення переходів)
    private readonly Dictionary<string, PingStatus> _lastStatus = new();

    // Всі інциденти в пам'яті (поточна сесія + завантажені з диску)
    private readonly List<DowntimeRecord> _records = new();
    private readonly object               _lock    = new();

    /// <summary>
    /// Сервери, які зараз Offline, але ще не "визріли" до MinIncidentDurationSeconds.
    /// Живе виключно в пам'яті — жодного DowntimeRecord, жодного SaveToDisk,
    /// жодного PublishSnapshot, поки інцидент не підтвердиться і не буде
    /// перенесений у _records. Дозволяє повністю уникнути зайвого I/O та
    /// UI-мерехтіння для коротких мережевих "миготінь".
    /// </summary>
    private readonly Dictionary<string, PendingOffline> _pendingOffline = new();

    private readonly record struct PendingOffline(
        DateTimeOffset FellAt, string ServerName, string Group);
    
    private readonly object               _saveLock = new();

    // ── Persistence ───────────────────────────────────────────────────────────

    private static readonly string LogDir =
        Path.Combine(AppContext.BaseDirectory, "logs");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private const string LogSource = "UptimeTracker";

    public UptimeTrackerService(
        IMessenger                    messenger,
        ILogger<UptimeTrackerService> logger,
        MaintenanceService            maintenance,
        IOptions<MonitoringSettings>  settings)
    {
        _messenger = messenger;
        _logger    = logger;
        _maintenance = maintenance;
        _settings  = settings.Value;
        _messenger.RegisterAll(this);
    }

    // ── IRecipient<MaintenanceChangedMessage> ───────────────────────────────

    public void Receive(MaintenanceChangedMessage message)
    {
        if (message.Action != MaintenanceAction.Started) return;

        var window = message.Window;
        bool changed = false;

        lock (_lock)
        {
            var affected = window.TargetGroup is not null
                ? _records.Where(r => r.ServerGroup.Equals(window.TargetGroup,
                    StringComparison.OrdinalIgnoreCase) && !r.IsResolved)
                : _records.Where(r => r.ServerIp == window.ServerIp && !r.IsResolved);

            foreach (var record in affected)
            {
                record.RecoveredAt        = DateTimeOffset.Now;
                record.ClosedByMaintenance = true;
                changed = true;

                _lastStatus[record.ServerIp] = PingStatus.Unknown;
            }

            // Прибираємо pending-записи (ще не промоутовані в DowntimeRecord) —
            // інакше вони можуть "визріти" вже після Maintenance зі старим
            // FellAt, що передував самому вікну обслуговування.
            var pendingKeysToRemove = window.TargetGroup is not null
                ? _pendingOffline.Where(kv => kv.Value.Group.Equals(
                        window.TargetGroup, StringComparison.OrdinalIgnoreCase))
                    .Select(kv => kv.Key).ToList()
                : (_pendingOffline.ContainsKey(window.ServerIp!)
                    ? [window.ServerIp!]
                    : []);

            foreach (var key in pendingKeysToRemove)
                _pendingOffline.Remove(key);
        }

        if (!changed) return;

        ScheduleSave();
        PublishSnapshot();

        _messenger.Send(AppLogEntryMessage.Info(LogSource,
            $"Відкриті інциденти для {window.DisplayName} закрито через Maintenance Mode."));
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LoadFromDisk(DateTimeOffset.Now);
        PublishSnapshot();
        _logger.LogInformation("UptimeTrackerService started.");
        _messenger.Send(AppLogEntryMessage.Info(LogSource, 
            "Uptime tracker started — відстеження переходів Online/Offline запущено."));
        return Task.CompletedTask;
    }

    // ── IRecipient ────────────────────────────────────────────────────────────

    private volatile bool _saveScheduled;

    public void Receive(PingBatchResultMessage message)
    {
        bool changed = false;

        lock (_lock)
        {
            foreach (var result in message.Value.Results)
            {
                if (result.Status is PingStatus.Unknown or PingStatus.Checking)
                {
                    _lastStatus[result.IP] = result.Status;
                    continue;
                }

                _lastStatus.TryGetValue(result.IP, out var prev);

                if (result.Status == PingStatus.Offline)
                {
                    bool underMaintenance = _maintenance.IsUnderMaintenance(result.IP, result.Group);

                    if (prev != PingStatus.Offline
                        && prev is not PingStatus.Unknown and not PingStatus.Checking
                        && !underMaintenance)
                    {
                        // Свіже падіння — НЕ пишемо DowntimeRecord одразу.
                        // Кладемо в pending і чекаємо MinIncidentDurationSeconds,
                        // перш ніж це стане "офіційним" інцидентом.
                        _pendingOffline[result.IP] =
                            new PendingOffline(DateTimeOffset.Now, result.Name, result.Group);
                    }
                    else if (!underMaintenance &&
                             _pendingOffline.TryGetValue(result.IP, out var pending))
                    {
                        // Сервер досі Offline — перевіряємо чи вже минув поріг.
                        var elapsed = DateTimeOffset.Now - pending.FellAt;
                        if (_settings.MinIncidentDurationSeconds <= 0
                            || elapsed.TotalSeconds >= _settings.MinIncidentDurationSeconds)
                        {
                            // Інцидент "визрів" — тільки тепер створюємо запис,
                            // пишемо на диск і показуємо в UI. FellAt лишається
                            // справжнім часом падіння, а не моментом промоції.
                            _records.Insert(0, new DowntimeRecord
                            {
                                ServerName  = pending.ServerName,
                                ServerIp    = result.IP,
                                ServerGroup = pending.Group,
                                FellAt      = pending.FellAt
                            });
                            _pendingOffline.Remove(result.IP);
                            changed = true;
                        }
                    }
                }
                else if (result.Status == PingStatus.Online && prev == PingStatus.Offline)
                {
                    // Якщо інцидент ще був лише Pending (не встиг визріти) —
                    // прибираємо його БЕЗ жодного звернення до диска чи UI.
                    if (!_pendingOffline.Remove(result.IP))
                    {
                        var open = _records.FirstOrDefault(
                            r => r.ServerIp == result.IP && !r.IsResolved);

                        if (open is not null)
                        {
                            open.RecoveredAt = DateTimeOffset.Now;
                            changed = true;
                        }
                    }
                }

                _lastStatus[result.IP] = result.Status;
            }
        }
        if (!changed) return;

        ScheduleSave();
        PublishSnapshot();
    }

    /// <summary>
    /// Відкладає SaveToDisk на 500мс і об'єднує кілька змін підряд
    /// (наприклад, кілька серверів впали в одному batch) в один запис на диск,
    /// замість File.Move на кожен окремий Receive.
    /// </summary>
    private void ScheduleSave()
    {
        if (_saveScheduled) return;
        _saveScheduled = true;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500).ConfigureAwait(false);

                // Скидаємо прапорець ДО старту запису — будь-яка подія,
                // що прийде під час SaveToDisk, знову поставить true
                // і запустить новий цикл накопичення, а не загубиться.
                _saveScheduled = false;

                SaveToDisk(DateTimeOffset.Now);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UptimeTrackerService: не вдалось зберегти інцидент на диск.");
                _saveScheduled = false;
            }
        });
    }

    // ── Public API для UptimeViewModel ────────────────────────────────────────

    /// <summary>Повертає копію списку інцидентів для початкового завантаження VM.</summary>
    public IReadOnlyList<DowntimeRecord> GetSnapshot()
    {
        lock (_lock) return _records.ToList();
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    /// <summary>
    /// Видаляє один запис з пам'яті та диску.
    /// Якщо запис активний (!IsResolved) — скидає _lastStatus[IP] на Online,
    /// щоб трекер коректно відстежував наступний перехід для цього сервера.
    /// Викликається з UI-потоку через RelayCommand у UptimeViewModel.
    /// </summary>
    public void DeleteRecord(DowntimeRecord record)
    {
        lock (_lock)
        {
            if (!record.IsResolved && _lastStatus.ContainsKey(record.ServerIp))
                _lastStatus[record.ServerIp] = PingStatus.Online;
            _records.Remove(record);
        }

        _ = Task.Run(() => SaveToDisk(DateTimeOffset.Now));
        PublishSnapshot();

        _messenger.Send(AppLogEntryMessage.Info(LogSource,
            $"Інцидент видалено вручну: {record.ServerName} ({record.ServerIp}), " +
            $"впав {record.FellAt:dd.MM HH:mm:ss}."));
    }

    public void ClearAllResolved()
    {
        int removed;
        lock (_lock)
        {
            removed = _records.RemoveAll(r => r.IsResolved);
        }

        if (removed == 0) return;

        _ = Task.Run(() => SaveToDisk(DateTimeOffset.Now));
        PublishSnapshot();

        _messenger.Send(AppLogEntryMessage.Info(LogSource,
            $"Очищено {removed} завершених інцидентів з історії."));
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    private static string FilePath(DateTimeOffset month)
        => Path.Combine(LogDir, $"uptime-{month:yyyy-MM}.json");

    private void LoadFromDisk(DateTimeOffset month)
    {
        var path = FilePath(month);
        if (!File.Exists(path)) return;

        try
        {
            var json    = File.ReadAllText(path);
            var records = JsonSerializer.Deserialize<List<DowntimeRecord>>(json, JsonOptions);

            if (records is null) return;

            lock (_lock)
            {
                foreach (var r in records)
                {
                    bool exists = _records.Any(x =>
                        x.ServerIp == r.ServerIp &&
                        x.FellAt   == r.FellAt);

                    if (!exists) _records.Add(r);
                }
                _records.Sort((a, b) => b.FellAt.CompareTo(a.FellAt));
            }

            _logger.LogInformation(
                "UptimeTrackerService: завантажено {Count} записів з {Path}",
                records.Count, path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UptimeTrackerService: помилка читання {Path}", path);
        }
    }

    private void SaveToDisk(DateTimeOffset month)
    {
        List<DowntimeRecord> snapshot;
        lock (_lock) snapshot = _records.ToList();

        lock (_saveLock)
        {
            try
            {
                Directory.CreateDirectory(LogDir);

                var thisMonth = snapshot
                    .Where(r => r.FellAt.Year  == month.Year &&
                                r.FellAt.Month == month.Month)
                    .ToList();

                var finalPath = FilePath(month);
                var tempPath  = finalPath + ".tmp";
                var json      = JsonSerializer.Serialize(thisMonth, JsonOptions);

                File.WriteAllText(tempPath, json);
                File.Move(tempPath, finalPath, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UptimeTrackerService: помилка збереження на диск.");
            }
        }
    }

    private void PublishSnapshot()
    {
        IReadOnlyList<DowntimeRecord> snapshot;
        lock (_lock) snapshot = _records.ToList();
        _messenger.Send(new UptimeUpdatedMessage(snapshot));
    }

    // ── IDisposable ──────────────────────────────────────────────────────────

    public override void Dispose()
    {
        _messenger.UnregisterAll(this);
        base.Dispose();
    }
}