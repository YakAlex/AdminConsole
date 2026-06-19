using AdminConsole.Core.Models;
using Microsoft.Extensions.Logging;
using System.Management;
using System.Net.NetworkInformation;

namespace AdminConsole.Services;

/// <summary>
/// On-demand читання CPU/RAM з віддаленої Windows-машини через WMI.
/// Викликається з ResourceMonitorViewModel при виборі remote сервера —
/// аналогічно до RemoteEventLogService.
///
/// Захист від таймаутів: той самий швидкий ping-чек перед WMI-запитом,
/// що й у RemoteEventLogService — WMI/DCOM таймаут за замовчуванням
/// може сягати 30-60с на офлайн машині.
/// </summary>
public sealed class RemoteResourceService
{
    private readonly ILogger<RemoteResourceService> _logger;

    private const int PingTimeoutMs = 1500;
    private const int WmiTimeoutSeconds = 8;

    public RemoteResourceService(ILogger<RemoteResourceService> logger)
    {
        _logger = logger;
    }

    public async Task<RemoteResourceResult> FetchAsync(
        string machineNameOrIp,
        CancellationToken ct = default)
    {
        bool reachable = await IsReachableAsync(machineNameOrIp, ct)
            .ConfigureAwait(false);

        if (!reachable)
        {
            _logger.LogDebug(
                "RemoteResourceService: {Machine} unreachable — skipping WMI fetch.",
                machineNameOrIp);

            return RemoteResourceResult.Unreachable();
        }

        try
        {
            var snapshot = await Task
                .Run(() => QueryWmi(machineNameOrIp), ct)
                .ConfigureAwait(false);

            return RemoteResourceResult.Success(snapshot);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex,
                "RemoteResourceService: access denied querying WMI on {Machine}.",
                machineNameOrIp);

            return RemoteResourceResult.Failed(
                "Access denied — current user lacks WMI permissions on this machine.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RemoteResourceService: failed to query WMI on {Machine}.",
                machineNameOrIp);

            return RemoteResourceResult.Failed(ex.Message);
        }
    }

    // ── WMI ───────────────────────────────────────────────────────────────────

    private static ResourceSnapshot QueryWmi(string machineName)
    {
        var scope = new ManagementScope(
            $@"\\{machineName}\root\cimv2",
            new ConnectionOptions
            {
                Timeout = TimeSpan.FromSeconds(WmiTimeoutSeconds),
                Impersonation = ImpersonationLevel.Impersonate,
                EnablePrivileges = true
            });

        scope.Connect();

        double cpuPercent = QueryCpuPercent(scope);
        (double usedGb, double totalGb) = QueryMemory(scope);

        double ramPercent = totalGb > 0
            ? Math.Round(usedGb / totalGb * 100.0, 1)
            : 0;

        return new ResourceSnapshot(
            CpuPercent: cpuPercent,
            RamUsedGb:  Math.Round(usedGb, 2),
            RamTotalGb: Math.Round(totalGb, 2),
            RamPercent: ramPercent,
            Timestamp:  DateTimeOffset.Now);
    }

    private static double QueryCpuPercent(ManagementScope scope)
    {
        using var searcher = new ManagementObjectSearcher(scope,
            new ObjectQuery("SELECT LoadPercentage FROM Win32_Processor"));

        // using для самої колекції — searcher.Get() повертає
        // ManagementObjectCollection, який тримає окремий COM RCW
        // над WMI-енумератором, незалежний від кожного ManagementObject.
        // Без явного dispose тут — повільний витік пам'яті при частих
        // запитах (а ми робимо WMI-запит кожні 5с на обраний remote сервер).
        using var results = searcher.Get();

        // Багатопроцесорна машина — беремо середнє по всіх логічних CPU.
        double sum = 0;
        int    count = 0;

        foreach (ManagementObject mo in results)
        {
            using (mo)
            {
                if (mo["LoadPercentage"] is not null)
                {
                    sum += Convert.ToDouble(mo["LoadPercentage"]);
                    count++;
                }
            }
        }

        return count > 0 ? Math.Round(sum / count, 1) : 0;
    }

    private static (double usedGb, double totalGb) QueryMemory(ManagementScope scope)
    {
        using var searcher = new ManagementObjectSearcher(scope,
            new ObjectQuery(
                "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem"));

        using var results = searcher.Get();

        foreach (ManagementObject mo in results)
        {
            using (mo)
            {
                // Значення WMI — у кілобайтах
                double totalKb = Convert.ToDouble(mo["TotalVisibleMemorySize"]);
                double freeKb  = Convert.ToDouble(mo["FreePhysicalMemory"]);

                double totalGb = totalKb / (1024.0 * 1024);
                double usedGb  = (totalKb - freeKb) / (1024.0 * 1024);

                return (usedGb, totalGb);
            }
        }

        return (0, 0);
    }

    private static async Task<bool> IsReachableAsync(string host, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping
                .SendPingAsync(host, PingTimeoutMs)
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

/// <summary>Результат спроби читання CPU/RAM з віддаленої машини.</summary>
public sealed class RemoteResourceResult
{
    public bool              IsReachable  { get; private init; }
    public bool              IsSuccess    { get; private init; }
    public string?           ErrorMessage { get; private init; }
    public ResourceSnapshot? Snapshot     { get; private init; }

    public static RemoteResourceResult Unreachable() => new()
    {
        IsReachable  = false,
        IsSuccess    = false,
        ErrorMessage = "Server is offline or unreachable."
    };

    public static RemoteResourceResult Success(ResourceSnapshot snapshot) => new()
    {
        IsReachable = true,
        IsSuccess   = true,
        Snapshot    = snapshot
    };

    public static RemoteResourceResult Failed(string error) => new()
    {
        IsReachable  = true,
        IsSuccess    = false,
        ErrorMessage = error
    };
}