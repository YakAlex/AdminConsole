using AdminConsole.Core.Models;
using Microsoft.Extensions.Logging;
using System.Management;

namespace AdminConsole.Services;

/// <summary>
/// On-demand читання Event Log з віддаленої Windows-машини.
///
/// ВАЖЛИВО: на відміну від локального EventLogService, який використовує
/// System.Diagnostics.EventLog(name, ".") — для remote-машин цей API
/// ненадійний з чистими IP-адресами (вимагає NetBIOS-резолву, легко
/// підвисає чи мовчки повертає 0 записів). Тому тут читання йде через
/// WMI (Win32_NTLogEvent) — той самий ManagementScope/DCOM шлях,
/// що вже стабільно працює у RemoteResourceService для CPU/RAM.
///
/// Захист від RPC-таймаутів: перед спробою читання WMI — швидкий
/// ping-чек (макс. 1.5с). Якщо сервер не відповідає — одразу повертаємо
/// порожній результат з причиною, не чекаючи 30-60 секунд на DCOM timeout.
/// </summary>
public sealed class RemoteEventLogService
{
    private readonly ILogger<RemoteEventLogService> _logger;

    private const int PingTimeoutMs     = 1500;
    private const int WmiTimeoutSeconds = 8;
    private const int FetchCount        = 20;

    // Win32_NTLogEvent.EventType: 1=Error, 2=Warning, 3=Information,
    // 4=Security Audit Success, 5=Security Audit Failure
    private const int EventTypeError = 1;

    public RemoteEventLogService(ILogger<RemoteEventLogService> logger)
    {
        _logger = logger;
    }

    public async Task<RemoteEventLogResult> FetchAsync(
        string machineNameOrIp,
        CancellationToken ct = default)
    {
        bool reachable = await EventLogReader
            .IsReachableAsync(machineNameOrIp, PingTimeoutMs, ct)
            .ConfigureAwait(false);

        if (!reachable)
        {
            _logger.LogDebug(
                "RemoteEventLogService: {Machine} unreachable — skipping Event Log fetch.",
                machineNameOrIp);

            return RemoteEventLogResult.Unreachable();
        }

        try
        {
            var entries = await Task
                .Run(() => QueryWmiEventLog(machineNameOrIp), ct)
                .ConfigureAwait(false);

            return RemoteEventLogResult.Success(entries);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex,
                "RemoteEventLogService: access denied querying Event Log on {Machine}.",
                machineNameOrIp);

            return RemoteEventLogResult.Failed(
                "Access denied — current user lacks permissions on this machine.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RemoteEventLogService: failed to read Event Log from {Machine}.",
                machineNameOrIp);

            return RemoteEventLogResult.Failed(ex.Message);
        }
    }

    // ── WMI ───────────────────────────────────────────────────────────────────

    private static List<EventLogEntry> QueryWmiEventLog(string machineName)
    {
        var scope = new ManagementScope(
            $@"\\{machineName}\root\cimv2",
            new ConnectionOptions
            {
                Timeout          = TimeSpan.FromSeconds(WmiTimeoutSeconds),
                Impersonation    = ImpersonationLevel.Impersonate,
                EnablePrivileges = true
            });

        scope.Connect();

        var results = new List<EventLogEntry>();
        DateTime cutoff = DateTime.Now.AddDays(-3);
        string dmtfCutoff = ManagementDateTimeConverter.ToDmtfDateTime(cutoff);
        
        foreach (var logName in new[] { "System", "Application" })
        {
            // Додаємо ЖОРСТКИЙ фільтр по часу: TimeGenerated >= '{dmtfCutoff}'
            // Це радикально пришвидшує Win32_NTLogEvent, бо він не сканує старі записи.
            var query = new ObjectQuery(
                $"SELECT TimeGenerated, SourceName, Message, EventCode, EventType " +
                $"FROM Win32_NTLogEvent " +
                $"WHERE LogFile='{logName}' " +
                $"AND EventType={EventTypeError} " +
                $"AND TimeGenerated >= '{dmtfCutoff}'");

            using var searcher = new ManagementObjectSearcher(scope, query)
            {
                Options = { Timeout = TimeSpan.FromSeconds(WmiTimeoutSeconds) }
            };

            using var collection = searcher.Get();

            // Win32_NTLogEvent не підтримує ORDER BY/TOP у WQL —
            // збираємо все що повернулось (вже відфільтроване по EventType)
            // і сортуємо/обрізаємо на клієнті.
            int scanned = 0;
            const int maxScanPerLog = 500; // захисний ліміт — не йдемо в нескінченність на величезних логах

            foreach (ManagementObject mo in collection)
            {
                using (mo)
                {
                    if (++scanned > maxScanPerLog) break;

                    results.Add(MapWmiEvent(mo));
                }
            }
        }

        return results
            .OrderByDescending(e => e.TimeGenerated)
            .Take(FetchCount)
            .ToList();
    }

    private static EventLogEntry MapWmiEvent(ManagementObject mo)
    {
        string timeGeneratedRaw = mo["TimeGenerated"]?.ToString() ?? string.Empty;
        DateTimeOffset timestamp = ParseWmiDateTime(timeGeneratedRaw);

        string source  = mo["SourceName"]?.ToString() ?? "Unknown";
        string message = EventLogReader.TrimMessage(mo["Message"]?.ToString() ?? string.Empty);
        string eventId = mo["EventCode"]?.ToString() ?? "0";

        return new EventLogEntry(
            Severity:      EventSeverity.Error, // запит вже відфільтрований по EventType=1
            Source:        source,
            Message:       message,
            EventId:       eventId,
            TimeGenerated: timestamp);
    }

    /// <summary>
    /// WMI повертає час у форматі DMTF: "yyyyMMddHHmmss.ffffff+UUU"
    /// (UUU — зсув у хвилинах від UTC). ManagementDateTimeConverter
    /// робить конвертацію офіційним способом замість ручного парсингу.
    /// </summary>
    private static DateTimeOffset ParseWmiDateTime(string dmtf)
    {
        if (string.IsNullOrWhiteSpace(dmtf)) return DateTimeOffset.MinValue;

        try
        {
            var dt = ManagementDateTimeConverter.ToDateTime(dmtf);
            return new DateTimeOffset(dt);
        }
        catch
        {
            return DateTimeOffset.MinValue;
        }
    }
    
}

/// <summary>Результат спроби читання віддаленого Event Log.</summary>
public sealed class RemoteEventLogResult
{
    public bool                          IsReachable  { get; private init; }
    public bool                          IsSuccess    { get; private init; }
    public string?                       ErrorMessage { get; private init; }
    public IReadOnlyList<EventLogEntry>  Entries      { get; private init; } = [];

    public static RemoteEventLogResult Unreachable() => new()
    {
        IsReachable  = false,
        IsSuccess    = false,
        ErrorMessage = "Server is offline or unreachable."
    };

    public static RemoteEventLogResult Success(IReadOnlyList<EventLogEntry> entries) => new()
    {
        IsReachable = true,
        IsSuccess   = true,
        Entries     = entries
    };

    public static RemoteEventLogResult Failed(string error) => new()
    {
        IsReachable  = true,
        IsSuccess    = false,
        ErrorMessage = error
    };
}