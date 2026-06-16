namespace AdminConsole.Core.Models;

public enum ServerType
{
    Windows,  // RDP + Restart + Shutdown
    Linux,    // SSH (PuTTY) — без RDP, без Restart/Shutdown
    Network   // switch/AP/NAS — тільки Ping-t
}

/// <summary>Один запис з масиву "Servers" у appsettings.json.</summary>
public sealed class ServerEntry
{
    public string     Name  { get; init; } = string.Empty;
    public string     IP    { get; init; } = string.Empty;
    public string     Group { get; init; } = string.Empty;

    /// <summary>
    /// Тип пристрою. Визначає які кнопки показуються у Ping Dashboard.
    /// Значення за замовчуванням — Windows (зворотна сумісність зі старим
    /// appsettings.json де поле Type відсутнє).
    /// </summary>
    public ServerType Type  { get; init; } = ServerType.Windows;
}