namespace AdminConsole.Core.Models;

/// <summary>Один сервер з масиву "Servers" у appsettings.json.</summary>
public sealed class ServerEntry
{
    public string Name { get; init; } = string.Empty;
    public string IP   { get; init; } = string.Empty;
    public string Group { get; init; } = string.Empty;
}