using AdminConsole.Views.Dialogs;
using MaterialDesignThemes.Wpf;

namespace AdminConsole.Services;

public sealed class DialogService : IDialogService
{
    public async Task<bool> ShowConfirmationAsync(
        string title,
        string body,
        string confirmLabel = "Confirm")
    {
        var dialog = new ConfirmActionDialog
        {
            DialogTitle = title,     // was Title — renamed to avoid new keyword
            BodyText    = body,
            ConfirmText = confirmLabel
        };

        var result = await DialogHost.Show(dialog, "RootDialogHost");
        return result is true;
    }
}