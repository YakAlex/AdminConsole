namespace AdminConsole.Services;

/// <summary>
/// Abstracts MaterialDesign DialogHost from ViewModels.
/// ViewModels call this interface — they never reference DialogHost directly.
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Shows a confirmation dialog and returns true if the user confirmed.
    /// </summary>
    Task<bool> ShowConfirmationAsync(string title, string body, string confirmLabel = "Confirm");
}