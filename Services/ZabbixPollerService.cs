using AdminConsole.Configuration;
using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdminConsole.Services;

public sealed class ZabbixPollerService : BackgroundService
{
    private readonly IMessenger                   _messenger;
    private readonly ILogger<ZabbixPollerService> _logger;
    private readonly MonitoringSettings           _settings;
    private readonly ZabbixApiClient              _client;
    private readonly CredentialStore              _credentials;
    private readonly ICredentialPrompt            _prompt;

    private static readonly int[] WatchedSeverities = [4, 5];
    private const string LogSource = "ZabbixPoller";
    private string? _sessionToken;

    public ZabbixPollerService(
        IMessenger messenger,
        ILogger<ZabbixPollerService> logger,
        IOptions<MonitoringSettings> settings,
        ZabbixApiClient client,
        CredentialStore credentials,
        ICredentialPrompt prompt)
    {
        _messenger   = messenger;
        _logger      = logger;
        _settings    = settings.Value;
        _client      = client;
        _credentials = credentials;
        _prompt      = prompt;

        _credentials.LoadZabbixFromVault();
    }

    // ── BackgroundService ────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.ZabbixUrl))
        {
            _logger.LogInformation("ZabbixPollerService: ZabbixUrl не налаштований — idle.");
            return;
        }

        // Перший запуск — отримати credentials якщо немає
        bool ready = await EnsureCredentialsAsync(stoppingToken);
        if (!ready) return;

        LogStarted();

        // Якщо user/password — автентифікуємось одразу
        if (!_credentials.ZabbixUsesApiToken)
        {
            await AuthenticateAsync(stoppingToken).ConfigureAwait(false);
            if (_sessionToken is null) return;
        }

        // Перший poll одразу
        await PollAsync(stoppingToken).ConfigureAwait(false);

        // Основний loop
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(
                TimeSpan.FromSeconds(_settings.ZabbixPollIntervalSeconds),
                stoppingToken).ConfigureAwait(false);

            await PollAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    // ── Credentials management ───────────────────────────────────────────────

    /// <summary>
    /// Перевіряє чи є credentials. Якщо немає — запитує через діалог.
    /// Повертає false якщо юзер скасував або вже скасовував раніше.
    /// </summary>
    private async Task<bool> EnsureCredentialsAsync(CancellationToken ct)
    {
        if (_credentials.HasZabbixCredentials)
            return true;

        if (_credentials.UserCancelledZabbixPrompt)
            return false;

        return await RequestFreshZabbixTokenAsync(
            reason: null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Показує діалог вводу токена.
    /// reason = null при першому запуску, non-null при повторному запиті
    /// (напр. "токен видалено адміністратором").
    /// Повертає true якщо токен отримано і збережено.
    /// </summary>
    private async Task<bool> RequestFreshZabbixTokenAsync(
        string? reason, CancellationToken ct)
    {
        if (reason is not null)
        {
            _messenger.Send(AppLogEntryMessage.Warning(LogSource,
                $"Zabbix: {reason}. Запитуємо новий токен…"));
        }

        var token = await _prompt.PromptZabbixTokenAsync();

        if (token is null)
        {
            _credentials.MarkZabbixCancelled();
            _messenger.Send(AppLogEntryMessage.Warning(LogSource,
                "Введення Zabbix токену скасовано. " +
                "Перезапустіть додаток щоб спробувати знову."));
            return false;
        }

        _credentials.StoreZabbixToken(token);
        _messenger.Send(AppLogEntryMessage.Info(LogSource,
            "Zabbix API токен збережено."));
        return true;
    }

    // ── Authentication (user/password mode only) ─────────────────────────────

    private async Task AuthenticateAsync(CancellationToken ct)
    {
        try
        {
            var (username, password) = _credentials.GetZabbix();
            _sessionToken = await _client.LoginAsync(
                _settings.ZabbixUrl, username, password, ct)
                .ConfigureAwait(false);

            if (_sessionToken is null)
            {
                _credentials.ClearZabbix();
                _messenger.Send(AppLogEntryMessage.Error(LogSource,
                    "Zabbix login failed — credentials видалено."));
            }
            else
            {
                _messenger.Send(AppLogEntryMessage.Success(LogSource,
                    "Zabbix authentication successful."));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ZabbixPollerService: login exception.");
            _messenger.Send(AppLogEntryMessage.Error(LogSource,
                $"Zabbix login exception: {ex.Message}"));
        }
    }

    // ── Poll ─────────────────────────────────────────────────────────────────

    private async Task PollAsync(CancellationToken ct)
    {
        // Читаємо актуальний стан кожного разу — може змінитись після
        // RequestFreshZabbixTokenAsync між циклами
        bool useApiToken = _credentials.ZabbixUsesApiToken;

        try
        {
            var (_, token) = _credentials.GetZabbix();
            string auth = useApiToken ? token : _sessionToken ?? string.Empty;

            var problems = await _client.GetActiveProblemsAsync(
                _settings.ZabbixUrl, auth, useApiToken,
                WatchedSeverities, ct).ConfigureAwait(false);

            _messenger.Send(new ZabbixProblemsUpdatedMessage(new ZabbixProblemsPayload(
                Problems: problems,
                ErrorMessage: null,
                FetchedAt: DateTimeOffset.Now)));
        }
        catch (OperationCanceledException) { }

        catch (ZabbixAuthException ex)
        {
            // Токен невалідний (видалено адміністратором або змінено)
            _logger.LogWarning("ZabbixPollerService: auth rejected — {Msg}", ex.Message);

            _credentials.ClearZabbix();
            _sessionToken = null;

            _messenger.Send(new ZabbixProblemsUpdatedMessage(new ZabbixProblemsPayload(
                Problems: [],
                ErrorMessage: "Токен відхилено — потрібна повторна авторизація",
                FetchedAt: DateTimeOffset.Now)));

            // Одразу показуємо діалог — не чекаємо наступного циклу
            bool obtained = await RequestFreshZabbixTokenAsync(
                reason: ex.Message,
                ct: CancellationToken.None).ConfigureAwait(false);

            if (!obtained)
            {
                _messenger.Send(AppLogEntryMessage.Warning(LogSource,
                    "Новий токен не отримано — opитування призупинено до перезапуску."));
            }
        }

        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ZabbixPollerService: poll failed.");
            _messenger.Send(AppLogEntryMessage.Error(LogSource,
                $"Zabbix poll failed: {ex.Message}"));
            _messenger.Send(new ZabbixProblemsUpdatedMessage(new ZabbixProblemsPayload(
                Problems: [],
                ErrorMessage: ex.Message,
                FetchedAt: DateTimeOffset.Now)));

            if (!useApiToken)
                await AuthenticateAsync(ct).ConfigureAwait(false);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void LogStarted()
    {
        bool useApiToken = _credentials.ZabbixUsesApiToken;
        _logger.LogInformation(
            "ZabbixPollerService started. Auth: {Mode}. Interval: {Interval}s.",
            useApiToken ? "API Token" : "User/Password",
            _settings.ZabbixPollIntervalSeconds);

        _messenger.Send(AppLogEntryMessage.Info(LogSource,
            $"Zabbix poller started " +
            $"({(useApiToken ? "API token" : "user/password")} auth). " +
            $"Polling every {_settings.ZabbixPollIntervalSeconds}s."));
    }

    // ── ZabbixAuthException ──────────────────────────────────────────────────

    /// <summary>
    /// Кидається ZabbixApiClient коли Zabbix повертає помилку авторизації
    /// у тілі відповіді (HTTP 200 + error field) або HTTP 401/403.
    /// Перехоплюється в PollAsync для запиту нового токена.
    /// </summary>
    public sealed class ZabbixAuthException : Exception
    {
        public ZabbixAuthException(string message) : base(message) { }
    }
}