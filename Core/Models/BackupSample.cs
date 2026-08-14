namespace AdminConsole.Core.Models;

/// <summary>
/// Один зафіксований розмір бекапу в rolling-історії. Зберігає сирі
/// дані (не лише агреговане середнє) навмисно — щоб перехід на
/// медіану/MAD у майбутньому не вимагав міграції формату файлу.
/// </summary>
public sealed class BackupSample
{
    public DateTimeOffset ObservedAt { get; init; }
    public long           SizeBytes  { get; init; }
}
