namespace AdminConsole.Core.Messages;

public enum CredentialTarget { Rdp, Zabbix }
public enum CredentialAction { Saved, Cleared }

/// <summary>
/// Публікується SettingsViewModel після збереження credentials.
/// RdpMonitorService і ZabbixPollerService підписуються і негайно
/// переривають Task.Delay щоб застосувати нові credentials без очікування.
/// Сервіси реагують тільки на Action = Saved — при Cleared нічого не роблять.
/// </summary>
public sealed class CredentialsChangedMessage
{
    public CredentialTarget Target { get; init; }
    public CredentialAction Action { get; init; }
}