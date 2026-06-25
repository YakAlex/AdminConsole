using AdminConsole.Core.Models;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AdminConsole.Services;

/// <summary>
/// Thin JSON-RPC 2.0 client for the Zabbix API.
/// Supports both API-token auth (Zabbix 5.4+) and
/// user/password session-token auth (older versions).
///
/// All methods are async and allocate minimally.
/// This class is stateless except for the injected HttpClient.
/// </summary>
public sealed class ZabbixApiClient
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ZabbixApiClient(HttpClient http)
    {
        _http = http;
    }

    // ── Authentication ────────────────────────────────────────────────────────

    /// <summary>
    /// Authenticates with username + password and returns a session token.
    /// Used for Zabbix versions older than 5.4 that do not support API tokens.
    /// Returns null if authentication fails.
    /// </summary>
    public async Task<string?> LoginAsync(
        string url, string user, string password,
        CancellationToken ct = default)
    {
        var request = BuildRequest("user.login", new JsonObject
        {
            ["username"] = user,
            ["password"] = password
        });

        var response = await PostAsync(url, request, ct).ConfigureAwait(false);
        return response?["result"]?.GetValue<string>();
    }
    
    // ── Connection test ───────────────────────────────────────────────────────

    /// <summary>
    /// Перевіряє доступність Zabbix API і валідність токену.
    /// Використовує легкий метод apiinfo.version — не потребує авторизації
    /// для отримання версії, але наступний крок (problem.get) перевірить токен.
    /// Повертає (true, "Zabbix 6.4.0", null) або (false, null, "повідомлення помилки").
    /// </summary>
    public async Task<(bool Success, string? Version, string? Error)> TestConnectionAsync(
        string url, string token, CancellationToken ct = default)
    {
        // Крок 1 — перевіряємо доступність API (без авторизації)
        try
        {
            var versionRequest = BuildRequest("apiinfo.version", new JsonObject());
            var versionResponse = await PostAsync(url, versionRequest, ct)
                .ConfigureAwait(false);

            var version = versionResponse?["result"]?.GetValue<string>();
            if (version is null)
                return (false, null, "Zabbix API не відповів коректно");

            // Крок 2 — перевіряємо токен через user.checkAuthentication.
            // Zabbix 6.0+: auth передається ТІЛЬКИ через Bearer заголовок або
            // поле "token" у params — НЕ через поле "auth" у тілі JSON-RPC.
            // BuildRequest без третього аргументу = не додає "auth" у тіло.
            var testRequest = BuildRequest("user.checkAuthentication", new JsonObject
            {
                ["token"] = token
            });

            var testResponse = await PostAsync(url, testRequest, ct, token)
                .ConfigureAwait(false);

            var errorNode = testResponse?["error"];
            if (errorNode is not null)
            {
                var errorData = errorNode["data"]?.GetValue<string>()
                    ?? errorNode["message"]?.GetValue<string>()
                    ?? "Невідома помилка";
                return (false, version, $"Токен недійсний: {errorData}");
            }

            return (true, version, null);
        }
        catch (OperationCanceledException)
        {
            return (false, null, "Перевірку скасовано");
        }
        catch (HttpRequestException ex)
        {
            return (false, null, $"Не вдалося підключитись: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, null, $"Помилка: {ex.Message}");
        }
    }

    // ── Problem fetch ─────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches active problems filtered to the given severities.
    /// auth: API token string (Zabbix 5.4+ set in Authorization header),
    ///       OR a session token obtained from LoginAsync.
    /// </summary>
    public async Task<List<ZabbixProblem>> GetActiveProblemsAsync(
    string  url,
    string  auth,
    bool    useApiToken,
    int[]   severities,
    CancellationToken ct = default)
{
    var parameters = new JsonObject
    {
        ["output"]      = new JsonArray("eventid", "name", "severity", "clock", "hosts"),
        ["severities"]  = new JsonArray(severities.Select(s => JsonValue.Create(s)).ToArray()),
        ["suppressed"]  = false,
        ["recent"]      = false,
        ["selectHosts"] = new JsonArray("host", "name"),
        ["sortorder"]   = "DESC",
        ["limit"]       = 200
    };

    var request = BuildRequest("problem.get", parameters, auth);
    var response = await PostAsync(url, request, ct, useApiToken ? auth : null).ConfigureAwait(false);
    if (response is null) return [];

    // ── Перевіряємо чи Zabbix повернув error у тілі відповіді ────────────────
    // Zabbix повертає HTTP 200 навіть при помилках авторизації,
    // але поле "error" присутнє, а "result" відсутнє.
    var errorNode = response["error"];
    if (errorNode is not null)
    {
        int    code = errorNode["code"]?.GetValue<int>() ?? 0;
        string data = errorNode["data"]?.GetValue<string>() ?? string.Empty;

        // Коди що означають невалідний токен / немає доступу:
        // -32602 = Invalid params / No permissions
        // -32500 = Application error (зазвичай auth)
        bool isAuthError = code is -32602 or -32500
            || data.Contains("No permissions", StringComparison.OrdinalIgnoreCase)
            || data.Contains("re-login", StringComparison.OrdinalIgnoreCase)
            || data.Contains("Not authorised", StringComparison.OrdinalIgnoreCase);

        if (isAuthError)
            throw new ZabbixPollerService.ZabbixAuthException(
                $"Zabbix відхилив токен (code={code}): {data}");

        throw new InvalidOperationException(
            $"Zabbix API error (code={code}): {data}");
    }

    var resultArray = response["result"]?.AsArray();
    if (resultArray is null) return [];

    var problems = new List<ZabbixProblem>(resultArray.Count);

    foreach (var node in resultArray)
    {
        if (node is null) continue;

        var eventId     = node["eventid"]?.GetValue<string>() ?? "0";
        var name        = node["name"]?.GetValue<string>()    ?? "(no description)";
        var severityStr = node["severity"]?.GetValue<string>() ?? "0";
        int.TryParse(severityStr, out int severityInt);
        var clockStr    = node["clock"]?.GetValue<string>()   ?? "0";

        var hostsArray  = node["hosts"]?.AsArray();
        var hostName    = hostsArray?.FirstOrDefault()?["name"]?.GetValue<string>()
                       ?? hostsArray?.FirstOrDefault()?["host"]?.GetValue<string>()
                       ?? "Unknown";

        var severity  = (ZabbixSeverity)Math.Clamp(severityInt, 0, 5);
        long.TryParse(clockStr, out long clockUnix);
        var startTime = DateTimeOffset.FromUnixTimeSeconds(clockUnix);        
        var age       = FormatAge(DateTimeOffset.UtcNow - startTime);

        problems.Add(new ZabbixProblem(
            EventId:     eventId,
            HostName:    hostName,
            Description: name,
            Severity:    severity,
            StartTime:   startTime,
            AgeDisplay:  age));
    }

    return problems;
}

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static JsonObject BuildRequest(
        string method,
        JsonObject parameters,
        string? auth = null)
    {
        var obj = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"]  = method,
            ["params"]  = parameters,
            ["id"]      = 1
        };

        if (auth is not null)
            obj["auth"] = auth;

        return obj;
    }

    private async Task<JsonNode?> PostAsync(
        string url,
        JsonObject body,
        CancellationToken ct,
        string? bearerToken = null)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };
        if (bearerToken is not null)
            msg.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        
        var response = await _http.SendAsync(msg, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content
            .ReadAsStringAsync(ct)
            .ConfigureAwait(false);
        return JsonNode.Parse(json);
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalDays >= 1)
            return $"{(int)age.TotalDays}d {age.Hours}h";
        if (age.TotalHours >= 1)
            return $"{(int)age.TotalHours}h {age.Minutes}m";
        if (age.TotalMinutes >= 1)
            return $"{(int)age.TotalMinutes}m";
        return "< 1m";
    }
}