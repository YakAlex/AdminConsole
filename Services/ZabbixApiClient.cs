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
        if (useApiToken)
        {
            // Zabbix 6.0+ requires the API token in the Authorization header.
            _http.DefaultRequestHeaders.Remove("Authorization");
            _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {auth}");
        }
        else
        {
            // Session token auth — token goes in the JSON body via "auth" field.
            // No Authorization header needed.
            _http.DefaultRequestHeaders.Remove("Authorization");
        }

        var parameters = new JsonObject
        {
            ["output"]      = new JsonArray("eventid", "name", "severity", "clock", "hosts"),
            ["severities"]  = new JsonArray(severities.Select(s => JsonValue.Create(s)).ToArray()),
            ["suppressed"]  = false,
            ["recent"]      = false,
            ["selectHosts"] = new JsonArray("host", "name"),
            //["sortfield"]   = new JsonArray("clock"),
            ["sortorder"]   = "DESC",
            ["limit"]       = 200
        };

        // For API token auth, pass the token in the auth field of the JSON body.
        // For session token auth, same mechanism.
        var request = BuildRequest("problem.get", parameters, auth);

        var response = await PostAsync(url, request, ct).ConfigureAwait(false);
        if (response is null) return [];

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

            // Resolve hostname from nested hosts array.
            var hostsArray  = node["hosts"]?.AsArray();
            var hostName    = hostsArray?.FirstOrDefault()?["name"]?.GetValue<string>()
                           ?? hostsArray?.FirstOrDefault()?["host"]?.GetValue<string>()
                           ?? "Unknown";

            var severity    = (ZabbixSeverity)Math.Clamp(severityInt, 0, 5);
            var startTime   = DateTimeOffset.FromUnixTimeSeconds(long.Parse(clockStr));
            var age         = FormatAge(DateTimeOffset.UtcNow - startTime);

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
        CancellationToken ct)
    {
        try
        {
            var content  = JsonContent.Create(body);
            var response = await _http
                .PostAsync(url, content, ct)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var json = await response.Content
                .ReadAsStringAsync(ct)
                .ConfigureAwait(false);
            //Console.WriteLine($"\n=== ZABBIX RAW RESPONSE ===\n{json}\n===========================\n");
            return JsonNode.Parse(json);
        }
        catch (OperationCanceledException) { return null; }
        catch { throw; }
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