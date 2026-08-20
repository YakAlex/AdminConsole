using AdminConsole.Core.Models;
using AdminConsole.Core.Models.Reports;
using Microsoft.Extensions.Options;

namespace AdminConsole.Services;

/// <summary>
/// Рахує SLA-звіт над уже наявними в пам'яті даними —
/// GetSnapshot() з UptimeTrackerService і статичним списком серверів
/// з appsettings.json (той самий IOptions, що й у PingMonitorService,
/// без залежності на сам пінг-цикл).
///
/// Generate() — чиста функція відносно свого входу (Now фіксується
/// один раз на початку) — жодного файлового I/O тут немає, тому
/// математику можна юніт-тестити окремо від диска й UI.
/// </summary>
public sealed class SlaReportService
{
    private readonly IReadOnlyList<ServerEntry> _servers;
    private readonly UptimeTrackerService       _uptime;

    public SlaReportService(
        IOptions<List<ServerEntry>> servers,
        UptimeTrackerService        uptime)
    {
        _servers = servers.Value.AsReadOnly();
        _uptime  = uptime;
    }

    /// <summary>
    /// "Флотова доступність" за період — яку частку [from, to] хоч ОДИН
    /// сервер був офлайн, через об'єднання (union) інтервалів простою.
    /// </summary>
    public double GetFleetAvailabilityPercent(DateTimeOffset from, DateTimeOffset to)
    {
        var now            = DateTimeOffset.Now;
        var effectiveTo    = Min(now, to);
        var periodDuration = effectiveTo - from;

        if (periodDuration <= TimeSpan.Zero) return 100.0;

        var intervals = _uptime.GetSnapshot()
            .Select(r => (
                Start: Max(r.FellAt, from),
                End:   Min(r.RecoveredAt ?? now, effectiveTo)))
            .Where(iv => iv.End > iv.Start)
            .OrderBy(iv => iv.Start)
            .ToList();

        var totalDown = TimeSpan.Zero;
        DateTimeOffset? curStart = null;
        DateTimeOffset? curEnd   = null;

        foreach (var iv in intervals)
        {
            if (curEnd is null || iv.Start > curEnd.Value)
            {
                if (curStart is not null)
                    totalDown += curEnd!.Value - curStart.Value;

                curStart = iv.Start;
                curEnd   = iv.End;
            }
            else if (iv.End > curEnd.Value)
            {
                curEnd = iv.End;
            }
        }

        if (curStart is not null)
            totalDown += curEnd!.Value - curStart.Value;

        return Math.Clamp(
            100.0 - totalDown.TotalSeconds / periodDuration.TotalSeconds * 100.0,
            0.0, 100.0);
    }

    public SlaReport Generate(SlaReportRequest request)
    {
        var now  = DateTimeOffset.Now;
        var from = request.From;
        var to   = request.To;
        var effectiveTo    = Min(now, to);
        var periodDuration = effectiveTo - from;

        if (periodDuration <= TimeSpan.Zero)
        {
            return new SlaReport
            {
                GeneratedAt          = now,
                From                 = from,
                To                   = to,
                OverallUptimePercent = null,
                Servers              = Array.Empty<ServerSlaEntry>(),
                MaintenanceAppendix  = Array.Empty<IncidentDetail>()
            };
        }

        var liveServers = _servers
            .Where(s => MatchesFilters(s.Group, s.Name, request))
            .ToList();

        var records = _uptime.GetSnapshot()
            .Where(r => MatchesFilters(r.ServerGroup, r.ServerName, request))
            .Where(r => ClippedDuration(r, from, effectiveTo, now) > TimeSpan.Zero)   // ← to → effectiveTo
            .ToList();

        var baseKeys = liveServers.Select(s => s.IP)
            .Union(records.Select(r => r.ServerIp))
            .Distinct()
            .ToList();

        var entries = new List<ServerSlaEntry>(baseKeys.Count);

        foreach (var ip in baseKeys)
        {
            var live          = liveServers.FirstOrDefault(s => s.IP == ip);
            var serverRecords = records.Where(r => r.ServerIp == ip).ToList();

            string name, group;
            if (live is not null)
            {
                name  = live.Name;
                group = live.Group;
            }
            else
            {
                // Видалений з конфіга — беремо ім'я/групу з останнього запису.
                var last = serverRecords.OrderByDescending(r => r.FellAt).First();
                name  = last.ServerName;
                group = last.ServerGroup;
            }

            var downtime            = TimeSpan.Zero;
            var maintenanceDowntime = TimeSpan.Zero;
            var incidentCount       = 0;
            var closedDurations     = new List<TimeSpan>();

            foreach (var r in serverRecords)
            {
                var clipped = ClippedDuration(r, from, effectiveTo, now);

                if (r.ClosedByMaintenance)
                    maintenanceDowntime += clipped;
                else
                {
                    downtime += clipped;
                    incidentCount++;
                }
                if (r.RecoveredAt is not null && !r.ClosedByMaintenance)
                    closedDurations.Add(r.RecoveredAt.Value - r.FellAt);
            }

            var uptimePercent = Math.Clamp(
                (periodDuration - downtime) / periodDuration * 100.0, 0.0, 100.0);

            TimeSpan? mttr = closedDurations.Count > 0
                ? TimeSpan.FromTicks((long)closedDurations.Average(d => d.Ticks))
                : null;

            var incidentDetails = serverRecords
                .OrderByDescending(r => r.FellAt)
                .Select(r => ToIncidentDetail(r, from, effectiveTo, now))
                .ToList();

            entries.Add(new ServerSlaEntry
            {
                ServerName                  = name,
                ServerIp                    = ip,
                ServerGroup                 = group,
                IsRemovedFromMonitoring     = live is null,
                UptimePercent               = uptimePercent,
                DowntimeInPeriod            = downtime,
                MaintenanceDowntimeInPeriod = maintenanceDowntime,
                IncidentCount               = incidentCount,
                Mttr                        = mttr,
                Incidents                   = incidentDetails
            });
        }

        entries = entries.OrderBy(e => e.UptimePercent).ToList(); // найгірші зверху

        var maintenanceAppendix = records
            .Where(r => r.ClosedByMaintenance)
            .OrderBy(r => r.ServerName)
            .ThenByDescending(r => r.FellAt)
            .Select(r => ToIncidentDetail(r, from, effectiveTo, now))
            .ToList();

        double? overallUptimePercent;
        if (entries.Count == 0)
        {
            overallUptimePercent = null;
        }
        else
        {
            // double-арифметика навмисно: periodDuration.Ticks * entries.Count
            // у long міг би переповнитись на довгих періодах × багатьох серверах.
            double totalDowntimeTicks = entries.Sum(e => (double)e.DowntimeInPeriod.Ticks);
            double totalPeriodTicks   = (double)periodDuration.Ticks * entries.Count;

            overallUptimePercent = Math.Clamp(
                100.0 - totalDowntimeTicks / totalPeriodTicks * 100.0, 0.0, 100.0);
        }

        return new SlaReport
        {
            GeneratedAt          = now,
            From                 = from,
            To                   = to,
            OverallUptimePercent = overallUptimePercent,
            Servers              = entries,
            MaintenanceAppendix  = maintenanceAppendix
        };
    }

    // ── Допоміжні методи ─────────────────────────────────────────────────────

    /// <summary>
    /// Єдина формула на весь сервіс: EffectiveEnd = RecoveredAt ?? Min(Now, To),
    /// потім перетин [FellAt, EffectiveEnd] з [From, To]. Однаково коректно
    /// обробляє і активний інцидент живого сервера, і "покинутий" відкритий
    /// інцидент видаленого сервера — без окремих гілок для кожного випадку.
    /// </summary>
    private static TimeSpan ClippedDuration(
        DowntimeRecord record, DateTimeOffset from, DateTimeOffset to, DateTimeOffset now)
    {
        var effectiveEnd = record.RecoveredAt ?? Min(now, to);
        var clippedEnd   = Min(effectiveEnd, to);
        var clippedStart = Max(record.FellAt, from);

        var duration = clippedEnd - clippedStart;
        return duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static IncidentDetail ToIncidentDetail(
        DowntimeRecord record, DateTimeOffset from, DateTimeOffset to, DateTimeOffset now)
        => new()
        {
            ServerName          = record.ServerName,
            ServerGroup         = record.ServerGroup,
            FellAt              = record.FellAt,
            RecoveredAt         = record.RecoveredAt,
            EffectiveDuration   = ClippedDuration(record, from, to, now),
            IsOngoing           = record.RecoveredAt is null,
            ClosedByMaintenance = record.ClosedByMaintenance
        };

    private static bool MatchesFilters(string group, string name, SlaReportRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.GroupFilter) &&
            !group.Equals(request.GroupFilter, StringComparison.OrdinalIgnoreCase))
            return false;

        // Підрядок, не точна рівність — той самий патерн пошуку,
        // що вже використовує UptimeViewModel.OnFilter для FilterServer.
        if (!string.IsNullOrWhiteSpace(request.ServerFilter) &&
            !name.Contains(request.ServerFilter, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }
}