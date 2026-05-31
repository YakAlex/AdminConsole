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

        // Завантажуємо збережений токен з Windows Credential Manager
        _credentials.LoadZabbixFromVault();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.ZabbixUrl))
        {
            _logger.LogInformation("ZabbixPollerService: ZabbixUrl не налаштований — idle.");
            return;
        }

        // Якщо немає credentials і юзер вже відмовився — не питаємо
        if (!_credentials.HasZabbixCredentials && _credentials.UserCancelledZabbixPrompt)
            return;

        // Якщо немає збережених credentials — запитуємо токен
        if (!_credentials.HasZabbixCredentials)
        {
            var token = await _prompt.PromptZabbixTokenAsync();
            if (token is null)
            {
                _credentials.MarkZabbixCancelled();
                _messenger.Send(AppLogEntryMessage.Warning(LogSource,
                    "Введення Zabbix токену скасовано. " +
                    "Перезапустіть додаток щоб спробувати знову."));
                return;
            }

            _credentials.StoreZabbixToken(token);
            _messenger.Send(AppLogEntryMessage.Info(LogSource,
                "Zabbix API токен збережено в Credential Manager."));
        }

        bool useApiToken = _credentials.ZabbixUsesApiToken;

        _logger.LogInformation(
            "ZabbixPollerService started. Auth: {Mode}. Polling every {Interval}s.",
            useApiToken ? "API Token" : "User/Password",
            _settings.ZabbixPollIntervalSeconds);

        _messenger.Send(AppLogEntryMessage.Info(LogSource,
            $"Zabbix poller started ({(useApiToken ? "API token" : "user/password")} auth). " +
            $"Polling every {_settings.ZabbixPollIntervalSeconds}s."));

        if (!useApiToken)
        {
            await AuthenticateAsync(stoppingToken).ConfigureAwait(false);
            if (_sessionToken is null) return;
        }

        await PollAsync(useApiToken, stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(
                TimeSpan.FromSeconds(_settings.ZabbixPollIntervalSeconds),
                stoppingToken).ConfigureAwait(false);

            await PollAsync(useApiToken, stoppingToken).ConfigureAwait(false);
        }
    }

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
                    "Zabbix login failed. Credentials видалено — буде запит при наступному циклі."));
            }
            else
            {
                _messenger.Send(AppLogEntryMessage.Success(LogSource,
                    "Zabbix authentication successful."));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ZabbixPollerService: Exception during login.");
            _messenger.Send(AppLogEntryMessage.Error(LogSource,
                $"Zabbix login exception: {ex.Message}"));
        }
    }

    private async Task PollAsync(bool useApiToken, CancellationToken ct)
    {
        try
        {
            var (_, token) = _credentials.GetZabbix();
            string auth = useApiToken ? token : _sessionToken ?? string.Empty;

            var problems = await _client.GetActiveProblemsAsync(
                _settings.ZabbixUrl, auth, useApiToken,
                WatchedSeverities, ct).ConfigureAwait(false);

            _messenger.Send(new ZabbixProblemsUpdatedMessage(new ZabbixProblemsPayload(
                Problems: problems, ErrorMessage: null, FetchedAt: DateTimeOffset.Now)));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ZabbixPollerService: poll failed.");
            _messenger.Send(AppLogEntryMessage.Error(LogSource,
                $"Zabbix poll failed: {ex.Message}"));
            _messenger.Send(new ZabbixProblemsUpdatedMessage(new ZabbixProblemsPayload(
                Problems: [], ErrorMessage: ex.Message, FetchedAt: DateTimeOffset.Now)));

            if (!useApiToken)
                await AuthenticateAsync(ct).ConfigureAwait(false);
        }
    }
}