namespace AdminConsole.Core.Messages;

/// <summary>
/// Публікується коли RDP credentials видалено через Settings.
/// RdpSessionViewModel реагує очищенням всіх сесій і статусів.
/// </summary>
public sealed class RdpCredentialsClearedMessage { }