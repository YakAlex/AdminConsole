using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.Messaging.Messages;

namespace AdminConsole.Core.Messages;

/// <summary>
/// Публікується BackupMonitorService раз на цикл (після SaveToDisk),
/// з deep-clone знімком усіх BackupCheckState. BackupsViewModel
/// підписується і оновлює UI.
///
/// Deep-clone навмисний: BackupCheckState має мутабельні set-властивості,
/// а той самий об'єкт продовжує жити в _states і мутуватись наступним
/// циклом фонового потоку — той самий клас проблем, що вже був
/// виправлений для DowntimeRecord/UptimeTrackerService (CloneRecord).
/// </summary>
public sealed class BackupStatusUpdatedMessage
    : ValueChangedMessage<IReadOnlyList<BackupCheckState>>
{
    public BackupStatusUpdatedMessage(IReadOnlyList<BackupCheckState> states)
        : base(states) { }
}