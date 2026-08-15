using CommunityToolkit.Mvvm.Messaging.Messages;

namespace AdminConsole.Core.Messages;

/// <summary>
/// Який фоновий сервіс моніторингу зачіпає перемикач у Settings.
/// </summary>
public enum MonitoredService
{
    Rdp,
    Zabbix,
    Backups
}

/// <summary>
/// Публікується у двох випадках:
///
/// 1) SettingsViewModel — коли користувач вручну перемикає ToggleButton
///    "RDP моніторинг" / "Zabbix моніторинг". Це "команда" — сигналізує
///    RdpMonitorService/ZabbixPollerService прокинутись і перевірити
///    UserSettingsService.Current заново (Pull), а також синхронізує
///    RdpSessionViewModel/ZabbixViewModel миттєво, не чекаючи наступного poll.
///
/// 2) SettingsViewModel — один раз у конструкторі (холодний старт),
///    щоб RdpSessionViewModel/ZabbixViewModel одразу знали початковий
///    стан (наприклад, якщо моніторинг вимкнули в минулій сесії) ще до
///    того як фонові сервіси взагалі стартують.
///
/// ВАЖЛИВО: фонові сервіси (RdpMonitorService, ZabbixPollerService)
/// використовують факт отримання цього повідомлення лише як тригер
/// "прокинься і перевір" — вони НЕ довіряють полю Enabled з самого
/// повідомлення, а завжди перечитують UserSettingsService.Current
/// самостійно (Pull). Це усуває ризик розсинхронізації між тим,
/// що написано в повідомленні, і тим, що зараз реально в конфігурації.
/// </summary>
public sealed class MonitoringToggledMessage : ValueChangedMessage<bool>
{
    public MonitoredService Service { get; }

    public MonitoringToggledMessage(MonitoredService service, bool enabled)
        : base(enabled)
    {
        Service = service;
    }
}