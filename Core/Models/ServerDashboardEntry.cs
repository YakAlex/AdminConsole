namespace AdminConsole.Core.Models;

/// <summary>
/// Запис у ComboBox Server Dashboard.
/// Може бути "localhost" (локальна машина) або будь-який ServerEntry з appsettings.
/// Лише Windows-сервери показуються — Linux і Network не підтримують WMI/EventLog.
/// </summary>
public sealed class ServerDashboardEntry
{
    public string Name       { get; }
    public string IP         { get; }
    public bool   IsLocal    { get; }

    /// <summary>Поточний ping-статус — оновлюється з PingBatchResultMessage.</summary>
    public PingStatus PingStatus { get; set; } = PingStatus.Unknown;

    private ServerDashboardEntry(string name, string ip, bool isLocal)
    {
        Name    = name;
        IP      = ip;
        IsLocal = isLocal;
    }

    public static ServerDashboardEntry Localhost() =>
        new("localhost (this machine)", "127.0.0.1", isLocal: true);

    public static ServerDashboardEntry FromServerEntry(ServerEntry entry) =>
        new(entry.Name, entry.IP, isLocal: false);

    // ComboBox відображає це
    public override string ToString() => Name;
}