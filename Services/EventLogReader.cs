using AdminConsole.Core.Models;
using System.Diagnostics.Eventing.Reader;
using EvtLogReader = System.Diagnostics.Eventing.Reader.EventLogReader;
using System.Net.NetworkInformation;

namespace AdminConsole.Services;

/// <summary>
/// Спільна логіка читання Windows Event Log через EventLogQuery (Eventing.Reader).
/// На відміну від System.Diagnostics.EventLog, Eventing.Reader працює через
/// курсори і XPath-запити — без індексованого доступу, тому стабільний
/// при активному записі в лог (немає ArgumentException при зміні Entries під час читання).
/// </summary>
public static class WinEventLogReader
{
    public const int FetchCount = 20;

    /// <summary>
    /// machineName: "." для localhost, або IP/hostname для віддаленої машини.
    /// since: null = початкове читання (останні FetchCount помилок за 3 дні),
    ///        not null = інкрементальний режим (тільки новіші за since).
    /// </summary>
    public static List<EventLogEntry> ReadErrors(string machineName, DateTimeOffset? since)
    {
        var results = new List<EventLogEntry>();

        foreach (var logName in new[] { "System", "Application" })
        {
            try
            {
                // XPath фільтр: тільки Error (Level=2) і FailureAudit (Keywords=0x10000000000000)
                // З часовим обмеженням щоб не сканувати весь лог.
                string timeFilter = since.HasValue
                    ? $"@SystemTime > '{since.Value.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffffZ}'"
                    : $"@SystemTime > '{DateTime.UtcNow.AddDays(-3):yyyy-MM-ddTHH:mm:ss.fffffffZ}'";

                // Level=2 → Error, Keywords включає FailureAudit (0x10000000000000)
                string xpath =
                    $"*[System[(Level=2 or Keywords='0x10000000000000') and TimeCreated[{timeFilter}]]]";

                // PathType.LogName — читаємо живий лог (не файл).
                // Direction.Backward — від нових до старих, тому зупиняємось після FetchCount.
                var query = new EventLogQuery(logName, PathType.LogName, xpath)
                {
                    ReverseDirection = true,
                    Session          = machineName == "."
                        ? null
                        : new EventLogSession(machineName)
                };

                using var reader = new EvtLogReader(query);

                int read = 0;
                EventRecord? record;

                while (read < FetchCount * 2 && (record = reader.ReadEvent()) is not null)
                {
                    using (record)
                    {
                        results.Add(MapRecord(record));
                        read++;
                    }
                }
            }
            catch (EventLogNotFoundException)
            {
                // Лог не існує на цій машині — пропускаємо мовчки
            }
            catch (UnauthorizedAccessException)
            {
                // Немає прав читати цей лог — пропускаємо мовчки
            }
            catch (Exception)
            {
                // RPC недоступний, мережева помилка тощо — пропускаємо мовчки
            }
        }

        return results
            .OrderByDescending(e => e.TimeGenerated)
            .Take(FetchCount)
            .ToList();
    }

    private static EventLogEntry MapRecord(EventRecord r)
    {
        // FormatDescription() може повернути null якщо провайдер не встановлений
        string rawMessage = string.Empty;
        try { rawMessage = r.FormatDescription() ?? string.Empty; }
        catch { rawMessage = r.Properties?.FirstOrDefault()?.Value?.ToString() ?? string.Empty; }

        // Level: 1=Critical, 2=Error, 3=Warning, 4=Information
        // Keywords: 0x10000000000000 = Audit Failure
        var severity = r.Level switch
        {
            1 => EventSeverity.Critical,
            2 => EventSeverity.Error,
            3 => EventSeverity.Warning,
            _ => EventSeverity.Information
        };

        // FailureAudit — якщо є keyword Audit Failure
        const long auditFailureKeyword = 0x10000000000000;
        if ((r.Keywords & auditFailureKeyword) != 0)
            severity = EventSeverity.Critical;

        return new EventLogEntry(
            Severity:      severity,
            Source:        r.ProviderName ?? "Unknown",
            Message:       TrimMessage(rawMessage),
            EventId:       r.Id.ToString(),
            TimeGenerated: r.TimeCreated.HasValue
                ? new DateTimeOffset(r.TimeCreated.Value)
                : DateTimeOffset.Now);
    }

    public static string TrimMessage(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "(no message)";
        var firstLine = raw
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? string.Empty;
        return firstLine.Length > 200 ? firstLine[..200] + "…" : firstLine;
    }

    public static async Task<bool> IsReachableAsync(
        string host, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping
                .SendPingAsync(host, timeoutMs)
                .WaitAsync(ct)
                .ConfigureAwait(false);
            return reply.Status == IPStatus.Success;
        }
        catch { return false; }
    }
}