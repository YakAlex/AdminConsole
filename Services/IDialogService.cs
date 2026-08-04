using AdminConsole.Core.Models;
namespace AdminConsole.Services;

/// <summary>
/// Abstracts MaterialDesign DialogHost from ViewModels.
/// ViewModels call this interface — they never reference DialogHost directly.
/// </summary>
public interface IDialogService
{
    Task<bool> ShowConfirmationAsync(string title, string body, string confirmLabel = "Confirm");

    /// <summary>
    /// Показує вибір тривалості Maintenance-вікна фіксованими пресетами
    /// (10/30/60 хв або без обмеження), без вільного текстового вводу.
    /// Повертає null, якщо користувач скасував діалог.
    /// </summary>
    Task<MaintenanceDurationChoice?> ShowMaintenanceDurationAsync(string serverName, string ip);
}