using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

using WinEventLog   = System.Diagnostics.EventLog;
using WinEventEntry = System.Diagnostics.EventLogEntry;
using WinEventType  = System.Diagnostics.EventLogEntryType;

namespace AdminConsole.Services;

/// <summary>
/// Читає Error/Critical записи з Windows Event Log (System + Application)
/// і публікує EventLogUpdatedMessage.
///
/// Оптимізація: зберігає _lastRead timestamp між ітераціями.
/// Перший запуск — читає останні FetchCount помилок за весь час.
/// Наступні запуски — сканує лише записи новіші за _lastRead,
/// зупиняючись одразу як тільки зустрів старий запис (early exit).
/// Повідомлення не надсилається якщо нових записів немає.
/// </summary>
public sealed class EventLogService : BackgroundService
{
    private readonly IMessenger               _messenger;
    private readonly ILogger<EventLogService> _logger;

    private const int FetchCount          = 20;
    private const int PollIntervalSeconds = 30;

    // Максимальна кількість записів для сканування при першому запуску.
    // Після першого запуску — не використовується (early exit по timestamp).
    private const int InitialMaxScan = 2000;

    // Зберігає час останнього прочитаного запису.
    // null = перший запуск, читаємо InitialMaxScan записів назад.
    // non-null = інкрементальний режим, читаємо лише нове.
    private DateTimeOffset? _lastRead = null;

    public EventLogService(
        IMessenger messenger,
        ILogger<EventLogService> logger)
    {
        _messenger = messenger;
        _logger    = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _logger.LogWarning("EventLogService: not running on Windows — disabled.");
            return;
        }

        _logger.LogInformation("EventLogService started.");

        await FetchAndPublishAsync();

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(
                TimeSpan.FromSeconds(PollIntervalSeconds),
                stoppingToken)
                .ConfigureAwait(false);

            await FetchAndPublishAsync();
        }

        _logger.LogInformation("EventLogService stopped.");
    }

    // ── Fetch ─────────────────────────────────────────────────────────────────

    private async Task FetchAndPublishAsync()
    {
        try
        {
            // Знімаємо час ДО читання — щоб не пропустити записи
            // що з'явились поки ми читали.
            var readStart = DateTimeOffset.Now;
            var since     = _lastRead;

            var entries = await Task
                .Run(() => ReadErrors(since))
                .ConfigureAwait(false);

            // Оновлюємо курсор лише якщо читання пройшло успішно
            _lastRead = readStart;

            // Не надсилаємо повідомлення якщо нічого нового немає.
            // Виключення: перший запуск (since == null) — завжди надсилаємо
            // щоб UI отримав початковий стан.
            if (since is not null && entries.Count == 0)
            {
                _logger.LogDebug("EventLogService: no new errors since {LastRead}.", since);
                return;
            }

            _logger.LogDebug(
                "EventLogService: found {Count} new error(s) since {Since}.",
                entries.Count, since);

            _messenger.Send(new EventLogUpdatedMessage(entries));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EventLogService: failed to read event logs.");
        }
    }

    // ── Читання записів ───────────────────────────────────────────────────────

    /// <summary>
    /// since == null  → початковий режим: сканує InitialMaxScan записів,
    ///                  повертає останні FetchCount помилок.
    /// since != null  → інкрементальний режим: сканує від кінця до since,
    ///                  зупиняється при першому записі старшому за since (early exit).
    /// </summary>
    private static List<EventLogEntry> ReadErrors(DateTimeOffset? since)
    {
        var results = new List<EventLogEntry>();

        foreach (var logName in new[] { "System", "Application" })
        {
            try
            {
                using var log = new WinEventLog(logName, ".");
                int count = log.Entries.Count;
                if (count == 0) continue;

                if (since is null)
                {
                    // ── Перший запуск: скануємо назад до InitialMaxScan ──────
                    int maxScan = Math.Min(count, InitialMaxScan);

                    for (int i = count - 1; i >= count - maxScan; i--)
                    {
                        if (results.Count >= FetchCount * 2) break;

                        WinEventEntry e = log.Entries[i];

                        if (e.EntryType is not (WinEventType.Error
                                            or WinEventType.FailureAudit))
                            continue;

                        results.Add(Map(e));
                    }
                }
                else
                {
                    // ── Інкрементальний режим: early exit по timestamp ───────
                    // Скануємо від найновішого до найстарішого.
                    // Як тільки зустріли запис старший або рівний since — стоп.
                    for (int i = count - 1; i >= 0; i--)
                    {
                        WinEventEntry e = log.Entries[i];

                        var generated = new DateTimeOffset(e.TimeGenerated);

                        // Early exit — цей і всі наступні записи вже читались
                        if (generated <= since.Value) break;

                        if (e.EntryType is not (WinEventType.Error
                                            or WinEventType.FailureAudit))
                            continue;

                        results.Add(Map(e));

                        // Захисний ліміт: якщо за 30 сек з'явилось
                        // аномально багато помилок — не тримаємо всі в пам'яті
                        if (results.Count >= FetchCount * 4) break;
                    }
                }
            }
            catch
            {
                // Log is inaccessible (permissions) — skip silently.
            }
        }

        return results
            .OrderByDescending(e => e.TimeGenerated)
            .Take(FetchCount)
            .ToList();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static EventLogEntry Map(WinEventEntry e) => new(
        Severity:      MapSeverity(e.EntryType),
        Source:        e.Source,
        Message:       TrimMessage(e.Message),
        EventId:       e.InstanceId.ToString(),
        TimeGenerated: new DateTimeOffset(e.TimeGenerated));

    private static EventSeverity MapSeverity(WinEventType t) => t switch
    {
        WinEventType.Error        => EventSeverity.Error,
        WinEventType.FailureAudit => EventSeverity.Critical,
        WinEventType.Warning      => EventSeverity.Warning,
        _                         => EventSeverity.Information
    };

    private static string TrimMessage(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "(no message)";

        var firstLine = raw
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? string.Empty;

        return firstLine.Length > 200
            ? firstLine[..200] + "…"
            : firstLine;
    }
}