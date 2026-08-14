using AdminConsole.Configuration;
using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdminConsole.Services;

/// <summary>
/// Опитує сконфігуровані BackupChecks (FileAge: вік + розмір проти
/// rolling-baseline, окремо Full/Diff). Двоетапна перевірка (Stage A/B)
/// делегована в BackupCheckEvaluator — цей сервіс відповідає лише за:
/// цикл, анти-флапінг (підтвердження стану лише після N однакових
/// "сирих" результатів поспіль), персистентність на диск і придушення
/// сповіщень під час Maintenance Windows.
///
/// Раз на цикл публікує BackupStatusUpdatedMessage (deep-clone знімок)
/// для UI-вкладки Backups; окремі переходи стану й далі йдуть в Logs tab
/// через AppLogEntryMessage.
/// </summary>
public sealed class BackupMonitorService : BackgroundService
{
    private readonly IMessenger                        _messenger;
    private readonly ILogger<BackupMonitorService>      _logger;
    private readonly MonitoringSettings                 _settings;
    private readonly IReadOnlyList<BackupCheckDefinition> _definitions;
    private readonly IReadOnlyDictionary<string, ServerEntry> _serverLookup;
    private readonly MaintenanceService                 _maintenance;
    private readonly BackupCheckEvaluator                _evaluator;

    private readonly ConcurrentDictionary<string, BackupCheckState> _states = new();

    private const string LogSource         = "BackupMonitor";
    private const int    MaxHistorySamples = 14;

    private static readonly string FilePath = Path.Combine(
        AppContext.BaseDirectory, "logs", "backups.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters    = { new JsonStringEnumConverter() }
    };

    private readonly object _saveLock = new();

    public BackupMonitorService(
        IMessenger                             messenger,
        ILogger<BackupMonitorService>          logger,
        IOptions<MonitoringSettings>           settings,
        IOptions<List<BackupCheckDefinition>>  backupChecks,
        IOptions<List<ServerEntry>>            servers,
        MaintenanceService                     maintenance,
        BackupCheckEvaluator                   evaluator)
    {
        _messenger    = messenger;
        _logger       = logger;
        _settings     = settings.Value;
        _definitions  = backupChecks.Value.AsReadOnly();
        _maintenance  = maintenance;
        _evaluator    = evaluator;

        _serverLookup = servers.Value
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        LoadFromDisk();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_definitions.Count == 0)
        {
            _logger.LogInformation("BackupMonitorService: BackupChecks не сконфігуровано — idle.");
            return;
        }

        foreach (var def in _definitions)
        {
            if (!_serverLookup.ContainsKey(def.ServerName))
            {
                _messenger.Send(AppLogEntryMessage.Warning(LogSource,
                    $"BackupChecks: '{def.ServerName}' не знайдено серед Servers у appsettings.json — " +
                    $"Maintenance-придушення для цього запису не працюватиме."));
            }
        }

        _messenger.Send(AppLogEntryMessage.Info(LogSource,
            $"Backup monitor запущено — {_definitions.Count} перевірок(и)."));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BackupMonitorService: неочікувана помилка циклу.");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromMinutes(_settings.BackupPollIntervalMinutes),
                    stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    // ── Цикл ─────────────────────────────────────────────────────────────────

    private async Task RunCycleAsync(CancellationToken ct)
    {
        foreach (var def in _definitions)
        {
            await CheckKindSafeAsync(def, BackupKind.Full, ct).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(def.DiffPattern))
                await CheckKindSafeAsync(def, BackupKind.Diff, ct).ConfigureAwait(false);
        }

        SaveToDisk();

        _messenger.Send(new BackupStatusUpdatedMessage(GetSnapshot()));
    }

    /// <summary>Обгортка навколо CheckKindAsync — одна невдала перевірка не має зупиняти весь цикл.</summary>
    private async Task CheckKindSafeAsync(BackupCheckDefinition def, BackupKind kind, CancellationToken ct)
    {
        try
        {
            await CheckKindAsync(def, kind, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BackupMonitorService: неочікувана помилка для {Server}/{Kind}",
                def.ServerName, kind);
        }
    }

    private async Task CheckKindAsync(BackupCheckDefinition def, BackupKind kind, CancellationToken ct)
    {
        var key   = StateKey(def.ServerName, kind);
        var state = _states.GetOrAdd(key, _ => new BackupCheckState
        {
            ServerName = def.ServerName,
            Kind       = kind
        });

        var raw = await _evaluator
            .EvaluateAsync(def, kind, state.History, ct)
            .ConfigureAwait(false);

        // ── Unknown-streak (окремо від анти-флапінгу підтвердженого стану) ──
        state.ConsecutiveUnknownCount = raw.Outcome == BackupOutcome.Unknown
            ? state.ConsecutiveUnknownCount + 1
            : 0;

        // ── LastConfirmed* — будь-яка НЕ-Unknown відповідь, незалежно від анти-флапінгу ──
        if (raw.Outcome != BackupOutcome.Unknown)
        {
            state.LastConfirmedAt      = DateTimeOffset.Now;
            state.LastConfirmedOutcome = raw.Outcome;
            state.LastError            = null;
        }
        else
        {
            state.LastError = raw.ErrorMessage;
        }

        // ── History — лише коли Stage B реально знайшов файл ──
        if (raw.Sample is not null)
        {
            state.History.Add(raw.Sample);
            while (state.History.Count > MaxHistorySamples)
                state.History.RemoveAt(0);
        }

        // ── Анти-флапінг: підтверджений Outcome (той, що бачить UI/алерти) ──
        if (raw.Outcome == state.Outcome)
        {
            state.ConsecutiveBadCount = 0;
            return;
        }

        state.ConsecutiveBadCount++;
        if (state.ConsecutiveBadCount < def.MinConsecutiveForAlert)
            return;

        var previous = state.Outcome;
        state.Outcome             = raw.Outcome;
        state.ConsecutiveBadCount = 0;

        OnConfirmedTransition(def, kind, previous, state.Outcome);
    }

    /// <summary>Викликається лише на ПІДТВЕРДЖЕНОМУ переході стану (після анти-флапінгу).</summary>
    private void OnConfirmedTransition(
        BackupCheckDefinition def, BackupKind kind,
        BackupOutcome previous, BackupOutcome current)
    {
        string label = $"{def.ServerName} ({kind})";

        bool underMaintenance =
            _serverLookup.TryGetValue(def.ServerName, out var entry) &&
            _maintenance.IsUnderMaintenance(entry.IP, entry.Group);

        if (current is BackupOutcome.Stale or BackupOutcome.Missing)
        {
            if (underMaintenance)
            {
                _messenger.Send(AppLogEntryMessage.Info(LogSource,
                    $"{label}: перехід у {current} придушено (активне Maintenance-вікно)."));
                return;
            }

            _messenger.Send(AppLogEntryMessage.Error(LogSource,
                $"{label}: перехід у {current} (було {previous})."));

            _messenger.Send(new BackupTransitionMessage
            {
                ServerName = def.ServerName,
                Kind       = kind,
                Previous   = previous,
                Current    = current
            });
            return;
        }

        if (current == BackupOutcome.SizeWarning)
        {
            _messenger.Send(AppLogEntryMessage.Warning(LogSource,
                $"{label}: розмір бекапу відхилився більш ніж на {def.SizeWarningThresholdPct}% від середнього."));
            return;
        }

        if (current == BackupOutcome.Unknown)
        {
            _messenger.Send(AppLogEntryMessage.Warning(LogSource,
                $"{label}: перевірка не відповідає (Unknown), було {previous}."));
            return;
        }

        // current == Ok
        if (previous is BackupOutcome.Stale or BackupOutcome.Missing or BackupOutcome.SizeWarning)
        {
            _messenger.Send(AppLogEntryMessage.Success(LogSource,
                $"{label}: відновлено (було {previous})."));
        }
    }

    private static string StateKey(string serverName, BackupKind kind) => $"{serverName}|{kind}";

    // ── Persistence (temp+rename, за зразком MaintenanceService) ───────────

    private void SaveToDisk()
    {
        try
        {
            var snapshot = _states.Values.ToList();
            var json     = JsonSerializer.Serialize(snapshot, JsonOptions);

            lock (_saveLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var tempPath = FilePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, FilePath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BackupMonitorService: помилка збереження {Path}", FilePath);
        }
    }

    private void LoadFromDisk()
    {
        if (!File.Exists(FilePath)) return;

        try
        {
            var json   = File.ReadAllText(FilePath);
            var states = JsonSerializer.Deserialize<List<BackupCheckState>>(json, JsonOptions);
            if (states is null) return;

            foreach (var s in states)
                _states[StateKey(s.ServerName, s.Kind)] = s;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BackupMonitorService: помилка читання {Path}", FilePath);
        }
    }

    // ── Public API (для UI-вкладки Backups, Фаза 3) ──────────────────────────

    public IReadOnlyList<BackupCheckState> GetSnapshot() =>
        _states.Values.Select(CloneState).ToList();

    /// <summary>
    /// Глибока копія для безпечної передачі за межі сервісу (UI-потік,
    /// messenger-підписники). BackupCheckState.Outcome/History мутабельні,
    /// а фоновий цикл продовжує писати в той самий об'єкт після того, як
    /// знімок пішов назовні — той самий принцип, що CloneRecord
    /// в UptimeTrackerService.
    /// </summary>
    private static BackupCheckState CloneState(BackupCheckState s) => new()
    {
        ServerName              = s.ServerName,
        Kind                    = s.Kind,
        Outcome                 = s.Outcome,
        LastConfirmedAt         = s.LastConfirmedAt,
        LastConfirmedOutcome    = s.LastConfirmedOutcome,
        ConsecutiveUnknownCount = s.ConsecutiveUnknownCount,
        ConsecutiveBadCount     = s.ConsecutiveBadCount,
        LastError               = s.LastError,
        History                 = s.History
            .Select(h => new BackupSample { ObservedAt = h.ObservedAt, SizeBytes = h.SizeBytes })
            .ToList()
    };
}

