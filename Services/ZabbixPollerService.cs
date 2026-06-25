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
    private CancellationTokenSource? _wakeUpCts;

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
        WeakReferenceMessenger.Default.Register<CredentialsChangedMessage>(
            this, (_, msg) => OnCredentialsChanged(msg));
    }
    
    private void OnCredentialsChanged(CredentialsChangedMessage msg)
    {
        if (msg.Target != CredentialTarget.Zabbix) return;
        if (msg.Action != CredentialAction.Saved) return;

        var cts = Interlocked.Exchange(ref _wakeUpCts, null);
        if (cts is null) return;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Ignored
        }
    }

    // ── BackgroundService ────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.ZabbixUrl))
        {
            _logger.LogInformation("ZabbixPollerService: ZabbixUrl не налаштований — idle.");
            return;
        }

        // Якщо credentials немає і юзер скасував діалог при старті —
        // не виходимо з ExecuteAsync, а входимо в цикл очікування.
        // Коли юзер збереже токен через Settings → CredentialsChangedMessage
        // прокине delayCts → цикл зробить poll з новими credentials.
        if (!_credentials.HasZabbixCredentials && !_credentials.UserCancelledZabbixPrompt)
        {
            bool ready = await EnsureCredentialsAsync(stoppingToken);
            if (!ready && stoppingToken.IsCancellationRequested) return;
            // Якщо скасував діалог (ready=false) але програма ще працює —
            // падаємо в основний цикл і чекаємо credentials через Settings.
        }

        // Credentials є (або щойно отримали) — запускаємось повноцінно
        if (_credentials.HasZabbixCredentials)
        {
            LogStarted();

            if (!_credentials.ZabbixUsesApiToken)
            {
                await AuthenticateAsync(stoppingToken).ConfigureAwait(false);
                if (_sessionToken is null && stoppingToken.IsCancellationRequested) return;
            }

            await PollAsync(stoppingToken).ConfigureAwait(false);
        }

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
                        TimeSpan.FromSeconds(_settings.ZabbixPollIntervalSeconds),
                        delayCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (!stoppingToken.IsCancellationRequested)
                {
                    _messenger.Send(AppLogEntryMessage.Info(LogSource,
                        "Zabbix credentials оновлено — запускаємо позачерговий poll."));
                }

                Interlocked.Exchange(ref _wakeUpCts, null);

                if (stoppingToken.IsCancellationRequested) break;

                if (!_credentials.HasZabbixCredentials)
                {
                    _messenger.Send(AppLogEntryMessage.Info(LogSource,
                        "Zabbix: credentials відсутні — poll пропущено."));
                    continue;
                }

                // Якщо це перший успішний wake-up після старту без credentials —
                // логуємо запуск і автентифікуємось якщо потрібно
                if (!_credentials.ZabbixUsesApiToken && _sessionToken is null)
                {
                    LogStarted();
                    await AuthenticateAsync(stoppingToken).ConfigureAwait(false);
                    if (_sessionToken is null) continue;
                }

                await PollAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Нормальне завершення при StopAsync — ігноруємо.
        }
        finally
        {
            _wakeUpCts = null;
            WeakReferenceMessenger.Default.Unregister<CredentialsChangedMessage>(this);
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
        while (true)
        {
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
                
                return; 
            }
            catch (OperationCanceledException) 
            { 
                return; 
            }
            catch (ZabbixAuthException ex)
            {
                _logger.LogWarning("ZabbixPollerService: auth rejected — {Msg}", ex.Message);

                _credentials.ClearZabbix();
                _sessionToken = null;

                _messenger.Send(new ZabbixProblemsUpdatedMessage(new ZabbixProblemsPayload(
                    Problems: [],
                    ErrorMessage: "Токен відхилено — потрібна повторна авторизація",
                    FetchedAt: DateTimeOffset.Now)));

                using var dialogCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                dialogCts.CancelAfter(TimeSpan.FromMinutes(5)); 
                
                bool obtained = await RequestFreshZabbixTokenAsync(
                    reason: ex.Message,
                    ct: dialogCts.Token).ConfigureAwait(false);

                if (!obtained)
                {
                    _messenger.Send(AppLogEntryMessage.Warning(LogSource,
                        "Новий токен не отримано — oпитування призупинено до перезапуску або зміни в налаштуваннях."));
                    return;
                }

                _messenger.Send(AppLogEntryMessage.Info(LogSource,
                    "Новий токен отримано через діалог — перевіряємо..."));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ZabbixPollerService: poll failed.");
                _messenger.Send(AppLogEntryMessage.Error(LogSource,
                    $"Zabbix poll failed: {ex.Message}"));

                _messenger.Send(new ZabbixProblemsUpdatedMessage(new ZabbixProblemsPayload(
                    Problems: null,
                    ErrorMessage: $"Помилка зв'язку: {ex.Message}",
                    FetchedAt: DateTimeOffset.Now)));

                if (!useApiToken && ex is not System.Net.Http.HttpRequestException)
                    await AuthenticateAsync(ct).ConfigureAwait(false);
                
                return;
            }
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