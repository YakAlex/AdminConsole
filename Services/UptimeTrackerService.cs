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
        
        LoadFromDisk(DateTimeOffset.Now);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("UptimeTrackerService started.");
        return Task.CompletedTask;
    }

    // ── IRecipient ────────────────────────────────────────────────────────────

    public void Receive(PingBatchResultMessage message)
    {
        bool changed = false;
        var logMessages = new List<AppLogEntryMessage>();

        lock (_lock)
        {
            foreach (var result in message.Value.Results)
            {
                // Пропускаємо Unknown і Checking — не реальні статуси
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

                    logMessages.Add(AppLogEntryMessage.Warning(LogSource,
                        $"{result.Name} ({result.IP}) перейшов у стан OFFLINE."));
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

                        logMessages.Add(AppLogEntryMessage.Info(LogSource,
                            $"{result.Name} ({result.IP}) відновлено. " +
                            $"Простій: {open.DurationDisplay}."));
                    }
                }

                _lastStatus[result.IP] = result.Status;
            }
        }

        // Відправляємо log-повідомлення поза lock
        foreach (var msg in logMessages)
            _messenger.Send(msg);

        if (!changed) return;

        SaveToDisk(DateTimeOffset.Now);
        PublishSnapshot();
    }

    // ── Public API для UptimeViewModel ────────────────────────────────────────

    /// <summary>Повертає копію списку інцидентів для початкового завантаження VM.</summary>
    public IReadOnlyList<DowntimeRecord> GetSnapshot()
    {
        lock (_lock) return _records.ToList();
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
            var records = JsonSerializer.Deserialize<List<DowntimeRecord>>(json);

            if (records is null) return;

            lock (_lock)
            {
                // Додаємо завантажені записи, уникаючи дублювань при повторному старті
                foreach (var r in records)
                {
                    bool exists = _records.Any(x =>
                        x.ServerIp == r.ServerIp &&
                        x.FellAt   == r.FellAt);

                    if (!exists) _records.Add(r);
                }

                // Сортуємо: найновіші зверху
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
        try
        {
            Directory.CreateDirectory(LogDir);

            List<DowntimeRecord> snapshot;
            lock (_lock) snapshot = _records.ToList();

            // Зберігаємо тільки записи поточного місяця у відповідний файл
            var thisMonth = snapshot
                .Where(r => r.FellAt.Year  == month.Year &&
                             r.FellAt.Month == month.Month)
                .ToList();

            var json = JsonSerializer.Serialize(thisMonth, JsonOptions);
            File.WriteAllText(FilePath(month), json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UptimeTrackerService: помилка збереження на диск.");
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