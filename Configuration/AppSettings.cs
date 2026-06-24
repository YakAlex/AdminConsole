namespace AdminConsole.Configuration;

public sealed class MonitoringSettings
{
    public const string SectionName = "Monitoring";

    public int    PingIntervalSeconds              { get; init; } = 30;
    
    /// <summary>
    /// Інтервал пінгу для серверів у стані Offline.
    /// Recovery loop пінгує тільки їх — швидше виявляє відновлення.
    /// Має бути менше PingIntervalSeconds. Мінімум 5с.
    /// </summary>
    public int    OfflinePingIntervalSeconds       { get; init; } = 10;
    public string ZabbixUrl                        { get; init; } = string.Empty;
    public int    ZabbixPollIntervalSeconds        { get; init; } = 60;
    public int    RdpPollIntervalSeconds           { get; init; } = 120;
    public int    LocalResourcePollIntervalSeconds { get; init; } = 3;
}

/// <summary>
/// Користувацькі налаштування програми.
/// Зберігаються у %LocalAppData%\AdminConsole\user_settings.json
/// Не входять до appsettings.json — змінюються під час роботи програми.
/// </summary>
public sealed class UserSettings
{
    /// <summary>
    /// true  — натискання хрестика ховає програму у трей.
    /// false — натискання хрестика закриває програму повністю.
    /// </summary>
    public bool CloseToTray { get; set; } = true;
}