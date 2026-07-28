using AdminConsole.Configuration;
using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AdminConsole.Services;

/// <summary>
/// Опитує термінальні сервери через "quser /server:HOSTNAME".
///
/// ВАЖЛИВО: використовуємо доменне ім'я (ServerEntry.Name), а НЕ IP.
/// quser /server:TSVR3 — працює через Named Pipes / NetBIOS.
/// quser /server:192.168.x.x — не працює (RPC over TCP, зазвичай заблоковано).
///
/// Авторизація через CredWrite (Credential Manager) — реєструємо credentials
/// перед quser, прибираємо після. Працює з Named Pipes транспортом WS2008R2.
/// </summary>
public sealed class RdpMonitorService : BackgroundService
{
    private readonly IMessenger                 _messenger;
    private readonly ILogger<RdpMonitorService> _logger;
    private readonly MonitoringSettings         _settings;
    private readonly IReadOnlyList<ServerEntry> _terminalServers;
    private readonly CredentialStore            _credentials;
    private readonly ICredentialPrompt          _prompt;
    private readonly UserSettingsService         _userSettings;

    private const string LogSource = "RdpMonitor";
    private const int    TimeoutMs = 30_000;
    private CancellationTokenSource? _wakeUpCts;

    // Кеш попереднього стану toggle (null = ще не перевіряли жодного разу).
    // Дозволяє логувати і слати MonitoringToggledMessage лише на РЕАЛЬНІЙ
    // зміні стану, а не на кожному циклі опитування (anti-spam, edge-case #2).
    private bool? _monitoringWasEnabled;

    private readonly ConcurrentDictionary<string, Dictionary<int, RdpSessionInfo>>
        _previousSessions = new();
    private readonly ConcurrentDictionary<string, bool> _firstPollDone = new();
    
    // ── Regex ────────────────────────────────────────────────────────────────
    // Формат WS2008R2 / WS2012+:
    //  USERNAME         SESSIONNAME    ID  STATE   IDLE TIME  LOGON TIME
    //  oleynikz         rdp-tcp#0      14  Active          .  11.06.2026 10:24
    //  yakymenko        rdp-tcp#1      15  Active          .  11.06.2026 10:29
    //  disconnecteduser                 3  Disc         1:30  11.06.2026 08:00

    private static readonly Regex ActiveRegex = new(
        @"^(?<user>\S+)\s+(?<session>\S+)\s+(?<id>\d+)\s+Active\s+(?<idle>\S+)\s+(?<logon>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        matchTimeout: TimeSpan.FromMilliseconds(500));

    private static readonly Regex DiscRegex = new(
        @"^(?<user>\S+)\s+(?<id>\d+)\s+Disc\s+(?<idle>\S+)\s+(?<logon>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        matchTimeout: TimeSpan.FromMilliseconds(500));

    public RdpMonitorService(
        IMessenger messenger,
        ILogger<RdpMonitorService> logger,
        IOptions<MonitoringSettings> settings,
        IOptions<List<ServerEntry>> servers,
        CredentialStore credentials,
        ICredentialPrompt prompt,
        UserSettingsService userSettings)
    {
        _messenger    = messenger;
        _logger       = logger;
        _settings     = settings.Value;
        _credentials  = credentials;
        _prompt       = prompt;
        _userSettings = userSettings;

        _terminalServers = servers.Value
            .Where(s => s.Group.Equals("Terminal Servers",
                StringComparison.OrdinalIgnoreCase))
            .ToList()
            .AsReadOnly();

        try
        {
            _credentials.LoadRdpFromVault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "RdpMonitorService: не вдалось завантажити credentials з Credential Manager.");
        }

        WeakReferenceMessenger.Default.Register<CredentialsChangedMessage>(
            this, (_, msg) => OnCredentialsChanged(msg));
        WeakReferenceMessenger.Default.Register<MonitoringToggledMessage>(
            this, (_, msg) => OnMonitoringToggled(msg));
    }

    // ── BackgroundService ────────────────────────────────────────────────────

    private void OnCredentialsChanged(CredentialsChangedMessage msg)
    {
        if (msg.Target != CredentialTarget.Rdp) return;
        if (msg.Action != CredentialAction.Saved) return;

        var cts = Interlocked.Exchange(ref _wakeUpCts, null);
        if (cts is null) return;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Реагує на перемикання RDP-моніторингу в Settings.
    /// НЕ довіряє полю Enabled з повідомлення — це лише сигнал "прокинься
    /// і перевір UserSettingsService.Current самостійно" (Pull, edge-case #2).
    /// Слугує для миттєвого відновлення опитування одразу після увімкнення,
    /// замість очікування до RdpPollIntervalSeconds.
    /// </summary>
    private void OnMonitoringToggled(MonitoringToggledMessage msg)
    {
        if (msg.Service != MonitoredService.Rdp) return;

        var cts = Interlocked.Exchange(ref _wakeUpCts, null);
        if (cts is null) return;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Перевіряє поточний стан RdpMonitoringEnabled і, лише при РЕАЛЬНІЙ
    /// зміні відносно попередньої перевірки, логує подію та шле
    /// MonitoringToggledMessage для синхронізації UI (edge-case #2 і #3).
    /// Виклик — щоразу перед credential-логікою (edge-case #1).
    /// </summary>
    private bool EvaluateMonitoringToggle()
    {
        bool enabled = _userSettings.Current.RdpMonitoringEnabled;

        if (_monitoringWasEnabled == enabled)
            return enabled; // стан не змінився — тиша, без спаму логів

        bool isColdStart = _monitoringWasEnabled is null;
        _monitoringWasEnabled = enabled;

        if (!enabled)
        {
            _messenger.Send(AppLogEntryMessage.Info(LogSource,
                "RDP моніторинг вимкнено в Settings."));
            _messenger.Send(new MonitoringToggledMessage(MonitoredService.Rdp, false));
        }
        else if (!isColdStart)
        {
            _messenger.Send(AppLogEntryMessage.Info(LogSource,
                "RDP моніторинг увімкнено — відновлюємо опитування."));
            _messenger.Send(new MonitoringToggledMessage(MonitoredService.Rdp, true));
        }

        return enabled;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_terminalServers.Count == 0)
        {
            _logger.LogInformation("RdpMonitorService: немає Terminal Servers — idle.");
            return;
        }

        foreach (var server in _terminalServers)
        {
            if (System.Net.IPAddress.TryParse(server.Name, out _))
            {
                _logger.LogWarning(
                    "RdpMonitorService: сервер '{Name}' має IP-адресу замість доменного імені.",
                    server.Name);
                _messenger.Send(AppLogEntryMessage.Warning(LogSource,
                    $"Конфігурація: '{server.Name}' — це IP, а не ім'я. " +
                    $"quser може не працювати. Виправ Name у appsettings.json."));
            }
        }

        // EDGE-CASE #1 (додатково): перевіряємо toggle ОДИН раз тут, щоб не логувати
        // "RDP monitor запущено", якщо моніторинг насправді вимкнено з минулої
        // сесії (без цього логи виглядали б як "запущено" відразу за "вимкнено").
        // Сам повторний виклик всередині PollAllServersAsync — ідемпотентний,
        // другого логу/повідомлення не буде, бо стан вже не зміниться.
        bool rdpMonitoringEnabled = EvaluateMonitoringToggle();

        if (rdpMonitoringEnabled)
        {
            _messenger.Send(AppLogEntryMessage.Info(LogSource,
                $"RDP monitor запущено — {_terminalServers.Count} сервер(ів). " +
                $"Використовуємо доменні імена для quser."));
        }

        await PollAllServersAsync(stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var delayCts = CancellationTokenSource
                    .CreateLinkedTokenSource(stoppingToken);

                Interlocked.Exchange(ref _wakeUpCts, delayCts);

                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(_settings.RdpPollIntervalSeconds),
                        delayCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (!stoppingToken.IsCancellationRequested)
                {
                    // Важливо: це пробудження може прийти як від CredentialsChangedMessage,
                    // так і від MonitoringToggledMessage (edge-case #2) — тому текст логу
                    // НЕ каже конкретно про credentials, щоб не вводити в оману.
                    _messenger.Send(AppLogEntryMessage.Info(LogSource,
                        "RDP: отримано сигнал пробудження — запускаємо позачерговий poll."));
                }

                Interlocked.Exchange(ref _wakeUpCts, null);

                if (stoppingToken.IsCancellationRequested) break;

                await PollAllServersAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _wakeUpCts = null;
            WeakReferenceMessenger.Default.Unregister<CredentialsChangedMessage>(this);
            WeakReferenceMessenger.Default.Unregister<MonitoringToggledMessage>(this);
        }
    }

    // ── Координація опитування ───────────────────────────────────────────────

    private async Task PollAllServersAsync(CancellationToken ct)
    {
        // EDGE-CASE #1: перевірка toggle — НАЙПЕРШИЙ рядок, ЩЕ ДО будь-якого
        // звернення до CredentialStore чи ICredentialPrompt. Якщо RDP-моніторинг
        // вимкнено — застосунок НІКОЛИ не запитає credentials, навіть якщо вони відсутні.
        if (!EvaluateMonitoringToggle())
            return;

        if (!_credentials.HasRdpCredentials && _credentials.UserCancelledRdpPrompt)
            return;

        if (!_credentials.HasRdpCredentials)
        {
            bool obtained = await RequestFreshRdpCredentialsAsync(reason: null, ct);
            if (!obtained) return;
        }

        var tasks = _terminalServers.Select(s => PollServerAsync(s, ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<bool> RequestFreshRdpCredentialsAsync(
        string? reason, CancellationToken ct)
    {
        if (reason is not null)
        {
            _messenger.Send(AppLogEntryMessage.Warning(LogSource,
                $"RDP credentials відхилено ({reason}). Запитуємо нові credentials…"));
        }

        var result = await _prompt.PromptAsync("Terminal Server (DOMAIN\\username)");

        if (result is null)
        {
            _credentials.MarkRdpCancelled();
            _messenger.Send(AppLogEntryMessage.Warning(LogSource,
                "Введення RDP credentials скасовано. " +
                "Перезапустіть додаток щоб спробувати знову."));
            return false;
        }

        _credentials.StoreRdp(result.Value.Username, result.Value.Password);
        _messenger.Send(AppLogEntryMessage.Info(LogSource,
            $"RDP credentials оновлено для: {result.Value.Username}"));
        return true;
    }

    // ── Опитування одного сервера ────────────────────────────────────────────

    private Task PollServerAsync(ServerEntry server, CancellationToken ct)
        => PollServerOnceAsync(server, ct);

    private async Task<string?> PollServerOnceAsync(ServerEntry server, CancellationToken ct)
    {
        string hostname = server.Name;

        try
        {
            if (!_credentials.HasRdpCredentials)
            {
                _messenger.Send(new RdpSessionsUpdatedMessage(new RdpSessionsPayload(
                    server.Name, server.IP, Sessions: [],
                    ErrorMessage: "Credentials недоступні — оновіть в Settings")));
                return null;
            }

            var (user, pass) = _credentials.GetRdp();

            if (!_credentials.StoreQuserSession(hostname, user, pass))
            {
                _messenger.Send(AppLogEntryMessage.Error(LogSource,
                    $"{hostname}: не вдалося зареєструвати credentials у Credential Manager."));
                _messenger.Send(new RdpSessionsUpdatedMessage(new RdpSessionsPayload(
                    server.Name, server.IP, Sessions: [],
                    ErrorMessage: "Помилка реєстрації credentials")));
                return null;
            }

            var (output, error, exitCode) = await RunQuserAsync(hostname, ct)
                .ConfigureAwait(false);

            string allText = (output + error).ToLowerInvariant();

            if (allText.Contains("logon failure") ||
                allText.Contains("1326")          ||
                allText.Contains("неверн")        ||
                allText.Contains("невірн"))
            {
                LogSessionChanges(server, []);
                _previousSessions[server.IP] = new Dictionary<int, RdpSessionInfo>();
                _messenger.Send(AppLogEntryMessage.Warning(LogSource,
                    $"{hostname}: невірний логін або пароль — оновіть credentials в Settings."));
                _messenger.Send(new RdpSessionsUpdatedMessage(new RdpSessionsPayload(
                    server.Name, server.IP, Sessions: [],
                    ErrorMessage: "Невірний логін або пароль — оновіть credentials в Settings")));
                return null;
            }

            if (allText.Contains("access is denied") ||
                allText.Contains("access denied"))
            {
                LogSessionChanges(server, []);
                _previousSessions[server.IP] = new Dictionary<int, RdpSessionInfo>();
                _messenger.Send(AppLogEntryMessage.Warning(LogSource,
                    $"{hostname}: Access Denied — можливо пароль змінено. Оновіть credentials в Settings."));
                _messenger.Send(new RdpSessionsUpdatedMessage(new RdpSessionsPayload(
                    server.Name, server.IP, Sessions: [],
                    ErrorMessage: "Access Denied — оновіть credentials в Settings")));
                return null;
            }

            if (allText.Contains("rpc server is unavailable") ||
                allText.Contains("1722")                      ||
                allText.Contains("0x000006ba"))
            {
                _messenger.Send(AppLogEntryMessage.Error(LogSource,
                    $"{hostname}: RPC недоступний. " +
                    $"Переконайся що в appsettings.json вказано доменне ім'я (не IP)."));
                _messenger.Send(new RdpSessionsUpdatedMessage(new RdpSessionsPayload(
                    server.Name, server.IP, Sessions: [],
                    ErrorMessage: "RPC недоступний — перевір ім'я сервера")));
                return null;
            }

            if (string.IsNullOrWhiteSpace(output) || exitCode == 1)
            {
                bool noUsers = allText.Contains("no user")           ||
                               allText.Contains("нет пользователей") ||
                               string.IsNullOrWhiteSpace(output);

                LogSessionChanges(server, []);
                _previousSessions[server.IP] = [];

                _messenger.Send(new RdpSessionsUpdatedMessage(new RdpSessionsPayload(
                    server.Name, server.IP,
                    Sessions: [],
                    ErrorMessage: noUsers ? null : $"Порожня відповідь (exit {exitCode})")));
                return null;
            }

            var sessions = ParseQuserOutput(output, server.Name, server.IP);
            LogSessionChanges(server, sessions);
            var newSnapshot = new Dictionary<int, RdpSessionInfo>();
            foreach (var s in sessions)
            {
                if (int.TryParse(s.SessionId, out int id))
                    newSnapshot[id] = s;
            }
            _previousSessions[server.IP] = newSnapshot;
            _messenger.Send(new RdpSessionsUpdatedMessage(new RdpSessionsPayload(
                server.Name, server.IP,
                Sessions: sessions,
                ErrorMessage: null)));
            return null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RdpMonitorService: помилка при опитуванні {Server}", hostname);
            _messenger.Send(AppLogEntryMessage.Error(LogSource,
                $"{hostname}: {ex.GetType().Name}: {ex.Message}"));
            _messenger.Send(new RdpSessionsUpdatedMessage(new RdpSessionsPayload(
                server.Name, server.IP, Sessions: [], ErrorMessage: ex.Message)));
            return null;
        }
        finally
        {
            _credentials.ClearQuserSession(hostname);
        }
    }

    // ── State Diffing ────────────────────────────────────────────────────────

    /// <summary>
    /// Порівнює поточний список сесій з попереднім знімком і логує тільки зміни.
    /// При першому poll для сервера — мовчки заповнює словник без логування,
    /// щоб не спамити "підключився" для вже існуючих сесій при старті програми.
    /// </summary>
    private void LogSessionChanges(ServerEntry server, List<RdpSessionInfo> currentSessions)
    {
        // Будуємо поточний знімок з валідними int SessionId
        var currentSnapshot = new Dictionary<int, RdpSessionInfo>();
        foreach (var s in currentSessions)
        {
            if (int.TryParse(s.SessionId, out int id))
                currentSnapshot[id] = s;
        }

        bool isFirst = _firstPollDone.TryAdd(server.IP, true);
        if (isFirst) return;

        _previousSessions.TryGetValue(server.IP, out var previousSnapshot);
        previousSnapshot ??= [];

        foreach (var (id, current) in currentSnapshot)
        {
            if (!previousSnapshot.TryGetValue(id, out var previous))
            {
                _messenger.Send(AppLogEntryMessage.Info(LogSource,
                    $"{current.Username} → connected to {server.Name} " +
                    $"(session #{id}, logon: {current.LogonTime})"));
                continue;
            }

            if (previous.State == RdpSessionState.Active &&
                current.State  == RdpSessionState.Disconnected)
            {
                _messenger.Send(AppLogEntryMessage.Warning(LogSource,
                    $"{current.Username} → session went idle on {server.Name} " +
                    $"(Active → Disconnected, logon: {current.LogonTime})"));
            }
            else if (previous.State == RdpSessionState.Disconnected &&
                     current.State  == RdpSessionState.Active)
            {
                _messenger.Send(AppLogEntryMessage.Info(LogSource,
                    $"{current.Username} → session resumed on {server.Name} " +
                    $"(Disconnected → Active, logon: {current.LogonTime})"));
            }
        }

        foreach (var (id, previous) in previousSnapshot)
        {
            if (!currentSnapshot.ContainsKey(id))
            {
                string duration = TryCalculateDuration(previous.LogonTime);
                string durationPart = duration.Length > 0 ? $", duration: {duration}" : string.Empty;

                _messenger.Send(AppLogEntryMessage.Info(LogSource,
                    $"{previous.Username} → disconnected from {server.Name} " +
                    $"(session #{id}, was connected since {previous.LogonTime}{durationPart})"));
            }
        }
    }

    /// <summary>
    /// Намагається розрахувати тривалість сесії з рядка LogonTime від quser.
    /// quser повертає формат "dd.MM.yyyy HH:mm" або "MM/dd/yyyy h:mm AM/PM".
    /// Повертає порожній рядок якщо розпарсити не вдалось.
    /// </summary>
    private static string TryCalculateDuration(string logonTime)
    {
        if (string.IsNullOrWhiteSpace(logonTime)) return string.Empty;
        
        string[] formats =
        [
            "dd.MM.yyyy HH:mm",
            "d.MM.yyyy H:mm",
            "MM/dd/yyyy h:mm tt",
            "M/d/yyyy h:mm tt",
            "dd.MM.yyyy H:mm",
        ];

        if (!DateTime.TryParseExact(logonTime.Trim(), formats,
            System.Globalization.CultureInfo.CurrentCulture,
            System.Globalization.DateTimeStyles.None,
            out var logon))
        {
            return string.Empty;
        }

        var duration = DateTime.Now - logon;

        if (duration.TotalDays >= 1)
            return $"{(int)duration.TotalDays}d {duration.Hours}h {duration.Minutes}m";
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        return $"{(int)duration.TotalMinutes}m";
    }

    // ── quser ────────────────────────────────────────────────────────────────

    private static async Task<(string Output, string Error, int ExitCode)> RunQuserAsync(
        string hostname, CancellationToken ct)
    {
        Encoding consoleEncoding;
        try
        {
            int oemPage = System.Globalization.CultureInfo
                .CurrentCulture.TextInfo.OEMCodePage;
            consoleEncoding = Encoding.GetEncoding(oemPage);
        }
        catch
        {
            consoleEncoding = new UTF8Encoding(false);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeoutMs);

        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName               = "quser.exe",
                Arguments              = $"/server:{hostname}",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
                StandardOutputEncoding = consoleEncoding,
                StandardErrorEncoding  = consoleEncoding
            },
            EnableRaisingEvents = true
        };

        p.Start();

        var outputTask = p.StandardOutput.ReadToEndAsync(cts.Token);
        var errorTask  = p.StandardError.ReadToEndAsync(cts.Token);

        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            string output = await outputTask.ConfigureAwait(false);
            string error  = await errorTask.ConfigureAwait(false);
            return (output, error, p.ExitCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            string partial = "";
            try { partial = await outputTask.ConfigureAwait(false); } catch { }
            return (partial, "Timeout: сервер не відповів за 30 секунд", -1);
        }
    }

    // ── Парсер виводу quser ──────────────────────────────────────────────────

    private List<RdpSessionInfo> ParseQuserOutput(
        string raw, string serverName, string serverIp)
    {
        var results = new List<RdpSessionInfo>();
        if (string.IsNullOrWhiteSpace(raw)) return results;

        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string normalized = line.Trim().TrimStart('>').Trim();

            if (string.IsNullOrWhiteSpace(normalized))                                    continue;
            if (normalized.StartsWith("USERNAME",    StringComparison.OrdinalIgnoreCase)) continue;
            if (normalized.StartsWith("SESSIONNAME", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                var match = ActiveRegex.Match(normalized);
                if (match.Success)
                {
                    results.Add(new RdpSessionInfo(
                        Username:    match.Groups["user"].Value.Trim(),
                        SessionName: match.Groups["session"].Value.Trim(),
                        SessionId:   match.Groups["id"].Value.Trim(),
                        State:       RdpSessionState.Active,
                        IdleTime:    match.Groups["idle"].Value.Trim(),
                        LogonTime:   match.Groups["logon"].Value.Trim(),
                        ServerName:  serverName,
                        ServerIp:    serverIp));
                    continue;
                }

                match = DiscRegex.Match(normalized);
                if (match.Success)
                {
                    results.Add(new RdpSessionInfo(
                        Username:    match.Groups["user"].Value.Trim(),
                        SessionName: "—",
                        SessionId:   match.Groups["id"].Value.Trim(),
                        State:       RdpSessionState.Disconnected,
                        IdleTime:    match.Groups["idle"].Value.Trim(),
                        LogonTime:   match.Groups["logon"].Value.Trim(),
                        ServerName:  serverName,
                        ServerIp:    serverIp));
                }
            }
            catch (RegexMatchTimeoutException)
            {
                _logger.LogWarning(
                    "RdpMonitorService: regex timeout on line from {Server}: {Line}",
                    serverName,
                    normalized.Length > 120 ? normalized[..120] + "…" : normalized);
            }
        }

        return results;
    }
}