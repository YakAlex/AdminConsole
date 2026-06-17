using AdminConsole.Configuration;
using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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

    private const string LogSource = "RdpMonitor";
    private const int    TimeoutMs = 30_000;

    // Парсимо вивід quser.
    // Формат WS2008R2 / WS2012+:
    //  USERNAME         SESSIONNAME    ID  STATE   IDLE TIME  LOGON TIME
    //  oleynikz         rdp-tcp#0      14  Active          .  11.06.2026 10:24
    //  yakymenko        rdp-tcp#1      15  Active          .  11.06.2026 10:29
    //  disconnecteduser                 3  Disc         1:30  11.06.2026 08:00
    //
    // Active: USERNAME SESSIONNAME ID STATE IDLE LOGON_DATE LOGON_TIME  (7+ токенів)
    // Disc:   USERNAME             ID STATE IDLE LOGON_DATE LOGON_TIME  (6 токенів, [1] — число)

    private static readonly Regex ActiveRegex = new(
        @"^(?<user>\S+)\s+(?<session>\S+)\s+(?<id>\d+)\s+Active\s+(?<idle>\S+)\s+(?<logon>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DiscRegex = new(
        @"^(?<user>\S+)\s+(?<id>\d+)\s+Disc\s+(?<idle>\S+)\s+(?<logon>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public RdpMonitorService(
        IMessenger messenger,
        ILogger<RdpMonitorService> logger,
        IOptions<MonitoringSettings> settings,
        IOptions<List<ServerEntry>> servers,
        CredentialStore credentials,
        ICredentialPrompt prompt)
    {
        _messenger   = messenger;
        _logger      = logger;
        _settings    = settings.Value;
        _credentials = credentials;
        _prompt      = prompt;

        _terminalServers = servers.Value
            .Where(s => s.Group.Equals("Terminal Servers",
                StringComparison.OrdinalIgnoreCase))
            .ToList()
            .AsReadOnly();

        _credentials.LoadRdpFromVault();
    }

    // ── BackgroundService ────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_terminalServers.Count == 0)
        {
            _logger.LogInformation("RdpMonitorService: немає Terminal Servers — idle.");
            return;
        }

        // Fail-fast валідація: quser потребує доменне ім'я, не IP.
        // IP-адреса як ім'я сервера → RPC over TCP → зазвичай заблоковано.
        foreach (var server in _terminalServers)
        {
            if (System.Net.IPAddress.TryParse(server.Name, out _))
            {
                _logger.LogWarning(
                    "RdpMonitorService: сервер '{Name}' має IP-адресу замість доменного імені. " +
                    "quser /server:IP не працює через RPC — використовуй NetBIOS/DNS ім'я.",
                    server.Name);

                _messenger.Send(AppLogEntryMessage.Warning(LogSource,
                    $"Конфігурація: '{server.Name}' — це IP, а не ім'я. " +
                    $"quser може не працювати. Виправ Name у appsettings.json."));
            }
        }

        _messenger.Send(AppLogEntryMessage.Info(LogSource,
            $"RDP monitor запущено — {_terminalServers.Count} сервер(ів). " +
            $"Використовуємо доменні імена для quser."));

        // Перший poll одразу при старті
        await PollAllServersAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(
                TimeSpan.FromSeconds(_settings.RdpPollIntervalSeconds),
                stoppingToken).ConfigureAwait(false);

            await PollAllServersAsync(stoppingToken);
        }
    }

    // ── Координація опитування ───────────────────────────────────────────────

    private async Task PollAllServersAsync(CancellationToken ct)
    {
        // Якщо юзер скасував діалог — не питаємо знову
        if (!_credentials.HasRdpCredentials && _credentials.UserCancelledRdpPrompt)
            return;

        // Запит credentials якщо відсутні
        if (!_credentials.HasRdpCredentials)
        {
            bool obtained = await RequestFreshRdpCredentialsAsync(
                reason: null, ct);
            if (!obtained) return;
        }

        // Паралельне опитування всіх Terminal Servers
        var tasks = _terminalServers.Select(s => PollServerAsync(s, ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Запитує нові RDP credentials через діалог.
    /// reason — причина запиту (null = перший запуск,
    ///          non-null = зміна пароля / невалідні credentials).
    /// Повертає true якщо credentials отримано і збережено.
    /// </summary>
    private async Task<bool> RequestFreshRdpCredentialsAsync(
        string? reason, CancellationToken ct)
    {
        if (reason is not null)
        {
            _messenger.Send(AppLogEntryMessage.Warning(LogSource,
                $"RDP credentials відхилено ({reason}). " +
                $"Запитуємо нові credentials…"));
        }

        var result = await _prompt.PromptAsync(
            "Terminal Server (DOMAIN\\username)");

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

    private const int MaxCredentialRetries = 3;

    private async Task PollServerAsync(ServerEntry server, CancellationToken ct)
    {
        for (int attempt = 0; attempt < MaxCredentialRetries; attempt++)
        {
            var retry = await PollServerOnceAsync(server, ct).ConfigureAwait(false);
            if (retry is null)
                return;

            bool gotNew = await RequestFreshRdpCredentialsAsync(retry, ct)
                .ConfigureAwait(false);
            if (!gotNew)
                return;
        }
    }

    /// <returns>null — завершено; non-null — причина повторного запиту credentials.</returns>
    private async Task<string?> PollServerOnceAsync(ServerEntry server, CancellationToken ct)
    {
        string hostname = server.Name;

        try
        {
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
                _credentials.ClearRdp();
                _messenger.Send(new RdpSessionsUpdatedMessage(new RdpSessionsPayload(
                    server.Name, server.IP, Sessions: [],
                    ErrorMessage: "Невірний логін або пароль — оновіть credentials")));
                return "невірний пароль або пароль змінено";
            }

            if (allText.Contains("access is denied") ||
                allText.Contains("access denied"))
            {
                _credentials.ClearRdp();
                _messenger.Send(AppLogEntryMessage.Warning(LogSource,
                    $"{hostname}: Access Denied — можливо пароль змінено. Запитуємо нові credentials…"));
                _messenger.Send(new RdpSessionsUpdatedMessage(new RdpSessionsPayload(
                    server.Name, server.IP, Sessions: [],
                    ErrorMessage: "Access Denied — оновіть пароль або перевір права")));
                return "Access Denied (можливо пароль змінено)";
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
                bool noUsers = allText.Contains("no user")            ||
                               allText.Contains("нет пользователей") ||
                               string.IsNullOrWhiteSpace(output);

                _messenger.Send(new RdpSessionsUpdatedMessage(new RdpSessionsPayload(
                    server.Name, server.IP,
                    Sessions: [],
                    ErrorMessage: noUsers ? null : $"Порожня відповідь (exit {exitCode})")));
                return null;
            }

            var sessions = ParseQuserOutput(output, server.Name, server.IP);
            _messenger.Send(AppLogEntryMessage.Info(LogSource,
                $"{hostname}: знайдено {sessions.Count} сесій."));
            _messenger.Send(new RdpSessionsUpdatedMessage(new RdpSessionsPayload(
                server.Name, server.IP,
                Sessions: sessions,
                ErrorMessage: null)));
            return null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RdpMonitorService: помилка при опитуванні {Server}", hostname);
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

        // Читаємо stdout/stderr асинхронно, щоб не блокувати thread-pool
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
            // Спрацював наш таймаут, а не зовнішня зупинка сервісу
            try { p.Kill(entireProcessTree: true); } catch { /* ігноруємо */ }
            string partial = "";
            try { partial = await outputTask.ConfigureAwait(false); } catch { }
            return (partial, "Timeout: сервер не відповів за 30 секунд", -1);
        }
    }

    // ── Парсер виводу quser ──────────────────────────────────────────────────

    /// <summary>
    /// Парсить вивід "quser /server:HOSTNAME".
    ///
    /// Формат виводу (реальний приклад з WS2008R2):
    ///  USERNAME              SESSIONNAME        ID  STATE   IDLE TIME  LOGON TIME
    ///  oleynikz              rdp-tcp#0          14  Active          .  11.06.2026 10:24
    ///  yakymenko             rdp-tcp#1          15  Active          .  11.06.2026 10:29
    ///
    /// Disconnected сесія (немає SESSIONNAME):
    ///  someuser                                  3  Disc         1:30  10.06.2026 08:00
    /// </summary>
    private static List<RdpSessionInfo> ParseQuserOutput(
        string raw, string serverName, string serverIp)
    {
        var results = new List<RdpSessionInfo>();
        if (string.IsNullOrWhiteSpace(raw)) return results;

        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Прибираємо '>' (маркер поточної сесії) і зайві пробіли
            string normalized = line.Trim().TrimStart('>').Trim();

            if (string.IsNullOrWhiteSpace(normalized))                                    continue;
            if (normalized.StartsWith("USERNAME", StringComparison.OrdinalIgnoreCase))    continue;
            if (normalized.StartsWith("SESSIONNAME", StringComparison.OrdinalIgnoreCase)) continue;

            // Спочатку пробуємо Active (є SESSIONNAME у виводі)
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

            // Потім пробуємо Disconnected (немає SESSIONNAME)
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

        return results;
    }
}