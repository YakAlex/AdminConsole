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
    /// <summary>
    /// Мінімальна тривалість (у секундах) даунтайму, щоб він потрапив
    /// у DowntimeRecord і зберігся на диск. Коротші "миготіння"
    /// (наприклад, одиничний втрачений ping-пакет через мережеву затримку)
    /// відкидаються при відновленні і не рахуються як SLA-інцидент.
    /// 0 — вимкнути фільтр (записувати все, як раніше).
    /// </summary>
    public int MinIncidentDurationSeconds { get; init; } = 10;
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
    
    /// <summary>
    /// true  — RdpMonitorService опитує Terminal Servers.
    /// false — сервіс не робить quser-запитів і не запитує RDP credentials,
    ///         навіть якщо вони відсутні (перевіряється ДО credential-логіки).
    /// </summary>
    public bool RdpMonitoringEnabled { get; set; } = true;

    /// <summary>
    /// true  — ZabbixPollerService опитує Zabbix API.
    /// false — сервіс не робить запитів і не запитує Zabbix токен,
    ///         навіть якщо він відсутній (перевіряється ДО credential-логіки).
    /// </summary>
    public bool ZabbixMonitoringEnabled { get; set; } = true;
    
    /// <summary>
    /// Chat ID Primary Admin в Telegram. Встановлюється один раз через
    /// /claim_admin з кодом, згенерованим у Settings. Null = ще не прив'язано.
    /// </summary>
    public long? TelegramPrimaryAdminChatId { get; set; }

    /// <summary>
    /// Список chat_id користувачів, яким Primary Admin дозволив read-only
    /// доступ до бота (окрім самого Primary Admin — той завжди має повний доступ).
    /// </summary>
    public List<long> TelegramAllowedChatIds { get; set; } = new();
}