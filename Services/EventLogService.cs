using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

// Alias the conflicting BCL type so our AdminConsole.Core.Models.EventLogEntry
// is the unqualified name throughout this file.
using WinEventLog = System.Diagnostics.EventLog;
using WinEventEntry = System.Diagnostics.EventLogEntry;
using WinEventType = System.Diagnostics.EventLogEntryType;

namespace AdminConsole.Services;

/// <summary>
/// Reads the last N Error/Critical entries from the Windows System and
/// Application event logs on a fixed interval and publishes
/// EventLogUpdatedMessage.
///
/// Runs on a background thread — zero UI thread involvement.
/// </summary>
public sealed class EventLogService : BackgroundService
{
    private readonly IMessenger               _messenger;
    private readonly ILogger<EventLogService> _logger;

    private const int FetchCount          = 20;
    private const int PollIntervalSeconds = 30;

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

        // Fetch immediately on startup, then repeat on interval.
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

    // -------------------------------------------------------------------------

    private async Task FetchAndPublishAsync()
    {
        try
        {
            var entries = await Task.Run(ReadRecentErrors).ConfigureAwait(false);
            _messenger.Send(new EventLogUpdatedMessage(entries));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EventLogService: failed to read event logs.");
        }
    }

    private static List<EventLogEntry> ReadRecentErrors()
    {
        var results = new List<EventLogEntry>();

        foreach (var logName in new[] { "System", "Application" })
        {
            try
            {
                using var log = new WinEventLog(logName, ".");

                int maxScan = Math.Min(log.Entries.Count, 2000);

                for (int i = log.Entries.Count - 1;
                     i >= log.Entries.Count - maxScan;
                     i--)
                {
                    if (results.Count >= FetchCount * 2) break;

                    WinEventEntry e = log.Entries[i];

                    if (e.EntryType is not (WinEventType.Error
                                        or WinEventType.FailureAudit))
                        continue;

                    // EventLogEntry here refers to AdminConsole.Core.Models.EventLogEntry
                    // because the BCL type is aliased to WinEventEntry above.
                    results.Add(new EventLogEntry(
                        Severity:      MapSeverity(e.EntryType),
                        Source:        e.Source,
                        Message:       TrimMessage(e.Message),
                        EventId:       e.InstanceId.ToString(),
                        TimeGenerated: new DateTimeOffset(e.TimeGenerated)));
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