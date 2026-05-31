using AdminConsole.Configuration;
using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AdminConsole.Services;

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
        _messenger       = messenger;
        _logger          = logger;
        _settings        = settings.Value;
        _credentials     = credentials;
        _prompt          = prompt;

        _terminalServers = servers.Value
            .Where(s => s.Group.Equals("Terminal Servers",
                StringComparison.OrdinalIgnoreCase))
            .ToList()
            .AsReadOnly();

        // Завантажуємо збережені credentials з Windows Credential Manager
        // Якщо є — діалог не з'явиться. Якщо немає — запитаємо при першому poll.
        _credentials.LoadRdpFromVault();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_terminalServers.Count == 0)
        {
            _logger.LogInformation("RdpMonitorService: немає Terminal Servers — idle.");
            return;
        }

        _messenger.Send(AppLogEntryMessage.Info(LogSource,
            $"RDP monitor запущено — {_terminalServers.Count} сервер(ів)."));

        await PollAllServersAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(
                TimeSpan.FromSeconds(_settings.RdpPollIntervalSeconds),
                stoppingToken).ConfigureAwait(false);

            await PollAllServersAsync(stoppingToken);
        }
    }

    private async Task PollAllServersAsync(CancellationToken ct)
    {
        if (!_credentials.HasRdpCredentials && _credentials.UserCancelledRdpPrompt)
            return;

        if (!_credentials.HasRdpCredentials)
        {
            var result = await _prompt.PromptAsync("Terminal Server (DOMAIN\\username)");
            if (result is null)
            {
                _credentials.MarkRdpCancelled();
                _messenger.Send(AppLogEntryMessage.Warning(LogSource,
                    "Введення RDP credentials скасовано. " +
                    "Перезапустіть додаток щоб спробувати знову."));
                return;
            }

            // StoreRdp зберігає в Windows Credential Manager автоматично
            _credentials.StoreRdp(result.Value.Username, result.Value.Password);
            _messenger.Send(AppLogEntryMessage.Info(LogSource,
                $"RDP credentials збережено для: {result.Value.Username}"));
        }

        var tasks = _terminalServers.Select(s => PollServerAsync(s, ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task PollServerAsync(ServerEntry server, CancellationToken ct)
    {
        bool credEstablished = false;
        string target = string.IsNullOrWhiteSpace(server.IP) ? server.Name : server.IP;

        try
        {
            var (user, pass) = _credentials.GetRdp();

            var (netOut, netErr, netCode) = await RunCmdAsync(
                $@"net use \\{target}\IPC$ ""{pass}"" /user:""{user}""", ct)
                .ConfigureAwait(false);

            string netAll = (netOut + netErr).ToLowerInvariant();

            if (netCode == 0 || netAll.Contains("already") || netAll.Contains("вже"))
            {
                credEstablished = true;
            }
            else if (netAll.Contains("logon failure") ||
                     netAll.Contains("invalid")       ||
                     netAll.Contains("неверн")        ||
                     netAll.Contains("невірн"))
            {
                _credentials.ClearRdp();
                _messenger.Send(AppLogEntryMessage.Error(LogSource,
                    $"{server.Name}: невірні credentials — видалено з Credential Manager. " +
                    "Буде запит при наступному циклі."));
                _messenger.Send(new RdpSessionsUpdatedMessage(new RdpSessionsPayload(
                    server.Name, server.IP, Sessions: [],
                    ErrorMessage: "Невірний логін або пароль")));
                return;
            }
            else
            {
                _messenger.Send(AppLogEntryMessage.Warning(LogSource,
                    $"{server.Name}: net use код {netCode}: {(netErr + netOut).Trim()}"));
            }

            var (output, error, exitCode) = await RunCmdAsync(
                $"quser /server:{target}", ct).ConfigureAwait(false);

            _messenger.Send(AppLogEntryMessage.Info(LogSource,
                $"[DIAG] {server.Name} | exit={exitCode} | out={output.Length}b"));

            var rawLines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < Math.Min(rawLines.Length, 6); i++)
                _messenger.Send(AppLogEntryMessage.Info(LogSource,
                    $"[DIAG] line[{i}]: »{rawLines[i].TrimEnd()}«"));

            if (!string.IsNullOrWhiteSpace(error))
                _messenger.Send(AppLogEntryMessage.Warning(LogSource,
                    $"[DIAG] {server.Name} stderr: {error.Trim()}"));

            string allText = (output + error).ToLowerInvariant();
            if (allText.Contains("access is denied") || allText.Contains("access denied"))
            {
                _credentials.ClearRdp();
                _messenger.Send(AppLogEntryMessage.Error(LogSource,
                    $"{server.Name}: Access Denied. Credentials видалено — буде запит при наступному циклі."));
                _messenger.Send(new RdpSessionsUpdatedMessage(new RdpSessionsPayload(
                    server.Name, server.IP, Sessions: [],
                    ErrorMessage: "Access Denied — перевір права акаунту")));
                return;
            }

            if (string.IsNullOrWhiteSpace(output))
            {
                bool noUsers = allText.Contains("no user") ||
                               allText.Contains("нет пользователей") ||
                               exitCode == 1;
                _messenger.Send(new RdpSessionsUpdatedMessage(new RdpSessionsPayload(
                    server.Name, server.IP, Sessions: [],
                    ErrorMessage: noUsers ? null : $"Немає відповіді (exit {exitCode})")));
                return;
            }

            var sessions = ParseQuserOutput(output, server.Name, server.IP);
            _messenger.Send(AppLogEntryMessage.Info(LogSource,
                $"{server.Name}: {sessions.Count} сесій знайдено."));
            _messenger.Send(new RdpSessionsUpdatedMessage(new RdpSessionsPayload(
                server.Name, server.IP, Sessions: sessions, ErrorMessage: null)));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RdpMonitorService: {Server}", server.Name);
            _messenger.Send(AppLogEntryMessage.Error(LogSource,
                $"{server.Name}: {ex.GetType().Name}: {ex.Message}"));
            _messenger.Send(new RdpSessionsUpdatedMessage(new RdpSessionsPayload(
                server.Name, server.IP, Sessions: [], ErrorMessage: ex.Message)));
        }
        finally
        {
            if (credEstablished)
            {
                try
                {
                    await RunCmdAsync(
                        $@"net use \\{target}\IPC$ /delete /yes",
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
            }
        }
    }

    private static async Task<(string Output, string Error, int ExitCode)> RunCmdAsync(
        string command, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName               = "cmd.exe",
                    Arguments              = $"/c {command}",
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                    StandardOutputEncoding = System.Text.Encoding.GetEncoding(866),
                    StandardErrorEncoding  = System.Text.Encoding.GetEncoding(866)
                }
            };
            p.Start();
            string output = p.StandardOutput.ReadToEnd();
            string error  = p.StandardError.ReadToEnd();
            bool   exited = p.WaitForExit(TimeoutMs);
            if (!exited) { try { p.Kill(); } catch { } }
            return (output, error, exited ? p.ExitCode : -1);
        }, ct).ConfigureAwait(false);
    }

    private static List<RdpSessionInfo> ParseQuserOutput(
        string raw, string serverName, string serverIp)
    {
        var results = new List<RdpSessionInfo>();
        if (string.IsNullOrWhiteSpace(raw)) return results;

        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string normalized = line.Trim().TrimStart('>').Trim();
            if (string.IsNullOrWhiteSpace(normalized)) continue;
            if (normalized.StartsWith("USERNAME", StringComparison.OrdinalIgnoreCase)) continue;

            var match = ActiveRegex.Match(normalized);
            if (match.Success)
            {
                results.Add(new RdpSessionInfo(
                    Username:    match.Groups["user"].Value,
                    SessionName: match.Groups["session"].Value,
                    SessionId:   match.Groups["id"].Value,
                    State:       RdpSessionState.Active,
                    IdleTime:    match.Groups["idle"].Value,
                    LogonTime:   match.Groups["logon"].Value.Trim(),
                    ServerName:  serverName,
                    ServerIp:    serverIp));
                continue;
            }

            match = DiscRegex.Match(normalized);
            if (match.Success)
            {
                results.Add(new RdpSessionInfo(
                    Username:    match.Groups["user"].Value,
                    SessionName: "Disconnected",
                    SessionId:   match.Groups["id"].Value,
                    State:       RdpSessionState.Disconnected,
                    IdleTime:    match.Groups["idle"].Value,
                    LogonTime:   match.Groups["logon"].Value.Trim(),
                    ServerName:  serverName,
                    ServerIp:    serverIp));
            }
        }
        return results;
    }
}