using AdminConsole.Core.Models;
using System.Net.NetworkInformation;
using WinEventLog   = System.Diagnostics.EventLog;
using WinEventEntry = System.Diagnostics.EventLogEntry;
using WinEventType  = System.Diagnostics.EventLogEntryType;

namespace AdminConsole.Services;

/// <summary>
/// Спільна логіка читання Windows Event Log — використовується
/// і локальним EventLogService (BackgroundService), і RemoteEventLogService
/// (on-demand виклик з ResourceMonitorViewModel).
/// </summary>
public static class EventLogReader
{
    public const int FetchCount      = 20;
    public const int InitialMaxScan  = 2000;

    /// <summary>
    /// machineName: "." для localhost, або IP/hostname для віддаленої машини.
    /// since: null = початкове читання (InitialMaxScan записів),
    ///        not null = інкрементальний режим (early exit по timestamp).
    /// </summary>
    public static List<EventLogEntry> ReadErrors(string machineName, DateTimeOffset? since)
    {
        var results = new List<EventLogEntry>();

        foreach (var logName in new[] { "System", "Application" })
        {
            try
            {
                using var log = new WinEventLog(logName, machineName);
                int count = log.Entries.Count;
                if (count == 0) continue;

                if (since is null)
                {
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
                    for (int i = count - 1; i >= 0; i--)
                    {
                        WinEventEntry e = log.Entries[i];

                        var generated = new DateTimeOffset(e.TimeGenerated);

                        if (generated <= since.Value) break;

                        if (e.EntryType is not (WinEventType.Error
                                            or WinEventType.FailureAudit))
                            continue;

                        results.Add(Map(e));

                        if (results.Count >= FetchCount * 4) break;
                    }
                }
            }
            catch
            {
                // Лог недоступний (права доступу, RPC недоступний) — пропускаємо мовчки.
            }
        }

        return results
            .OrderByDescending(e => e.TimeGenerated)
            .Take(FetchCount)
            .ToList();
    }

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

    public static string TrimMessage(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "(no message)";

        var firstLine = raw
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? string.Empty;

        return firstLine.Length > 200
            ? firstLine[..200] + "…"
            : firstLine;
    }

    /// <summary>
    /// Спільна перевірка доступності хоста через ICMP ping.
    /// Винесена сюди щоб уникнути дублювання між RemoteResourceService
    /// та RemoteEventLogService — єдине місце для зміни логіки і таймаутів.
    /// </summary>
    public static async Task<bool> IsReachableAsync(
        string host, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var ping  = new Ping();
            var reply = await ping
                .SendPingAsync(host, timeoutMs)
                .WaitAsync(ct)
                .ConfigureAwait(false);

            return reply.Status == IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }
}
