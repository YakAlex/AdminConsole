using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
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
    : BackgroundService, IRecipient<PingBatchResultMessage>, IDisposable
{
    private readonly IMessenger                   _messenger;
    private readonly ILogger<UptimeTrackerService> _logger;

    // Поточний статус кожного IP (для визначення переходів)
    private readonly Dictionary<string, PingStatus> _lastStatus = new();

    // Всі інциденти в пам'яті (поточна сесія + завантажені з диску)
    private readonly List<DowntimeRecord> _records = new();
    private readonly object               _lock    = new();
    
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
        ILogger<UptimeTrackerService> logger)
    {
        _messenger = messenger;
        _logger    = logger;
        _messenger.RegisterAll(this);
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

                if (result.Status == PingStatus.Offline
                    && prev != PingStatus.Offline
                    && prev is not PingStatus.Unknown and not PingStatus.Checking)
                {
                    var record = new DowntimeRecord
                    {
                        ServerName  = result.Name,
                        ServerIp    = result.IP,
                        ServerGroup = result.Group,
                        FellAt      = DateTimeOffset.Now
                    };

                    _records.Insert(0, record);
                    changed = true;
                    
                }
                else if (result.Status == PingStatus.Online
                         && prev == PingStatus.Offline)
                {
                    var open = _records.FirstOrDefault(
                        r => r.ServerIp == result.IP && !r.IsResolved);

                    if (open is not null)
                    {
                        open.RecoveredAt = DateTimeOffset.Now;
                        changed = true;
                    }
                }

                _lastStatus[result.IP] = result.Status;
            }
        }
        if (!changed) return;
        try
        {
            SaveToDisk(DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UptimeTrackerService: не вдалось зберегти інцидент на диск.");
        }

        PublishSnapshot();
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

        Task.Run(() => SaveToDisk(DateTimeOffset.Now));
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

        Task.Run(() => SaveToDisk(DateTimeOffset.Now));
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