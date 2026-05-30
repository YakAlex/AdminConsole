namespace AdminConsole.Core.Models;

/// <summary>
/// Represents one server entry from the "Servers" array in appsettings.json.
/// Replaces the Configuration-level ServerEntry — keep only this one.
/// Update Configuration/AppSettings.cs to remove the duplicate if present.
/// </summary>
public sealed class ServerEntry
{
    public string Name { get; init; } = string.Empty;
    public string IP   { get; init; } = string.Empty;
    public string Group { get; init; } = string.Empty;
}