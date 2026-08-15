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
    
    /// <summary>Захист від некоректного/від'ємного/нульового appsettings — той самий патерн, що MinOfflineIntervalSec у PingMonitorService.</summary>
    private const int    MinBackupPollIntervalMinutes = 1;
    
    /// <summary>Скільки циклів поспіль Unknown, перш ніж один раз надіслати попередження (без спаму).</summary>
    private const int    UnknownEscalationThreshold = 3;

    private static readonly string FilePath = Path.Combine(
        AdminConsole.Utils.AppPaths.BaseDirectory, "logs", "backups.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters    = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Захищає мутації полів BackupCheckState (Outcome/History/лічильники)
    /// від паралельного читання в GetSnapshot() — викликається з UI-потоку
    /// (BackupsViewModel) і з потоку long-polling бота (TelegramBotService)
    /// одночасно з фоновим циклом CheckKindAsync. Той самий принцип, що
    /// _lock у UptimeTrackerService для _records.
    /// </summary>
    private readonly object _stateLock = new();

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
        
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LoadFromDisk();
        if (_definitions.Count == 0)
        {
            _logger.LogInformation("BackupMonitorService: BackupChecks не сконфігуровано — idle.");
            return;
        }

        foreach (var def in _definitions)
        {
            string searchKey = string.IsNullOrWhiteSpace(def.Host) ? def.Name : def.Host;
            
            if (!_serverLookup.ContainsKey(searchKey))
            {
                _messenger.Send(AppLogEntryMessage.Warning(LogSource,
                    $"BackupChecks: Хост '{searchKey}' (для бекапу '{def.Name}') не знайдено у списку серверів | " +
                            $"Maintenance-придушення не працюватиме."));
            }
        }

        var pollInterval = Math.Max(
            _settings.BackupPollIntervalMinutes,
            MinBackupPollIntervalMinutes);

        _messenger.Send(AppLogEntryMessage.Info(LogSource,
            $"Backup monitor запущено — {_definitions.Count} перевірок(и), " +
            $"інтервал: {pollInterval} хв."));

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
                    TimeSpan.FromMinutes(pollInterval),
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
                def.Name, kind);
        }
    }

    private async Task CheckKindAsync(BackupCheckDefinition def, BackupKind kind, CancellationToken ct)
    {
        var key   = StateKey(def.Name, kind);
        var state = _states.GetOrAdd(key, _ => new BackupCheckState
        {
            Name = def.Name,
            Host = def.Host,
            Kind = kind
        });

var raw = await _evaluator
            .EvaluateAsync(def, kind, state.History, ct)
            .ConfigureAwait(false);

        (bool shouldNotify, BackupOutcome previous, BackupOutcome current) transition = default;
        bool crossedUnknownThreshold;

        lock (_stateLock)
        {
            // ── Unknown-streak (окремо від анти-флапінгу підтвердженого стану) ──
            state.ConsecutiveUnknownCount = raw.Outcome == BackupOutcome.Unknown
                ? state.ConsecutiveUnknownCount + 1
                : 0;
            crossedUnknownThreshold = state.ConsecutiveUnknownCount == UnknownEscalationThreshold;

            bool neverConfirmedYet = state.LastConfirmedAt is null;

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

            // ── Перше підтвердження після рестарту/першого запуску —
            // підтверджуємо одразу, без очікування MinConsecutiveForAlert.
            // До першого підтвердженого результату немає "попереднього
            // стану", який анти-флапінг мав би захищати — очікування тут
            // лише затримує коректний перший статус (до BackupPollIntervalMinutes
            // × MinConsecutiveForAlert хвилин) без жодної компенсуючої користі.
            if (neverConfirmedYet && raw.Outcome != BackupOutcome.Unknown)
            {
                var firstPrevious = state.Outcome;
                state.Outcome             = raw.Outcome;
                state.ConsecutiveBadCount = 0;
                state.LastRawOutcome      = null;

                transition = (true, firstPrevious, state.Outcome);
            }
            // ── Анти-флапінг: підтверджений Outcome (той, що бачить UI/алерти) ──
            else if (raw.Outcome == state.Outcome)
            {
                state.ConsecutiveBadCount = 0;
                state.LastRawOutcome      = raw.Outcome;
            }
            else
            {
                // Рахуємо серію ОДНАКОВИХ "сирих" результатів поспіль, що
                // відрізняються від підтвердженого Outcome — а не будь-яку
                // зміну raw.Outcome взагалі (це і був баг: Ok→Stale→Missing
                // підтверджувало перехід, хоча жоден сирий результат не
                // повторився двічі поспіль).
                state.ConsecutiveBadCount = raw.Outcome == state.LastRawOutcome
                    ? state.ConsecutiveBadCount + 1
                    : 1;
                state.LastRawOutcome = raw.Outcome;

                if (state.ConsecutiveBadCount >= def.MinConsecutiveForAlert)
                {
                    var previous = state.Outcome;
                    state.Outcome             = raw.Outcome;
                    state.ConsecutiveBadCount = 0;
                    state.LastRawOutcome      = null;

                    transition = (true, previous, state.Outcome);
                }
            }
        }

        if (crossedUnknownThreshold)
        {
            _messenger.Send(AppLogEntryMessage.Warning(LogSource,
                $"{def.Name} ({kind}): перевірка недоступна вже " +
                $"{UnknownEscalationThreshold} циклів поспіль."));
        }

        if (transition.shouldNotify)
            OnConfirmedTransition(def, kind, transition.previous, transition.current);
    }

    /// <summary>Викликається лише на ПІДТВЕРДЖЕНОМУ переході стану (після анти-флапінгу).</summary>
    private void OnConfirmedTransition(
        BackupCheckDefinition def, BackupKind kind,
        BackupOutcome previous, BackupOutcome current)
    {
        string label = $"{def.Name} ({kind})";

        bool underMaintenance =
            _serverLookup.TryGetValue(def.Host, out var entry) &&
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
                ServerName = def.Name,
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
            AdminConsole.Utils.JsonFileStore.SaveAtomic(FilePath, snapshot, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BackupMonitorService: помилка збереження {Path}", FilePath);
        }
    }

    private void LoadFromDisk()
    {
        try
        {
            var states = AdminConsole.Utils.JsonFileStore.TryLoad<List<BackupCheckState>>(FilePath, JsonOptions);
            if (states is null) return;

            // Валідні ключі — лише ті (Name, Kind), що досі є в BackupChecks
            // конфігурації. Якщо запис прибрали/перейменували в appsettings.json,
            // його стан у backups.json більше не підвантажується — інакше він
            // "зависає" в UI/Telegram зі старим статусом (MISSING/STALE) назавжди,
            // бо ExecuteAsync ніколи більше не викличе CheckKindAsync для нього.
            var validKeys = _definitions
                .SelectMany(def => string.IsNullOrWhiteSpace(def.DiffPattern)
                    ? new[] { StateKey(def.Name, BackupKind.Full) }
                    : new[] { StateKey(def.Name, BackupKind.Full), StateKey(def.Name, BackupKind.Diff) })
                .ToHashSet();

            int skipped = 0;
            foreach (var s in states)
            {
                var key = StateKey(s.Name, s.Kind);
                if (!validKeys.Contains(key))
                {
                    skipped++;
                    continue;
                }

                _states[key] = s;
            }

            if (skipped > 0)
            {
                string msg = $"Пропущено {skipped} застарілих запис(ів) — більше не знайдено у BackupChecks конфігурації.";
                
                _logger.LogInformation("BackupMonitorService: {Message}", msg);
                
                _messenger.Send(AppLogEntryMessage.Info(LogSource, msg));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BackupMonitorService: помилка читання {Path}", FilePath);
        }
    }

    // ── Public API (для UI-вкладки Backups, Фаза 3) ──────────────────────────

    public IReadOnlyList<BackupCheckState> GetSnapshot()
    {
        lock (_stateLock)
            return _states.Values.Select(CloneState).ToList();
    }

    /// <summary>
    /// Глибока копія для безпечної передачі за межі сервісу (UI-потік,
    /// messenger-підписники). BackupCheckState.Outcome/History мутабельні,
    /// а фоновий цикл продовжує писати в той самий об'єкт після того, як
    /// знімок пішов назовні — той самий принцип, що CloneRecord
    /// в UptimeTrackerService.
    /// </summary>
    private static BackupCheckState CloneState(BackupCheckState s) => new()
    {
        Name                    = s.Name,
        Host                    = s.Host,
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

