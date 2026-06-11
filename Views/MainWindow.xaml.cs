using AdminConsole.Services;
using AdminConsole.ViewModels;
using System.Windows;

namespace AdminConsole.Views;

public partial class MainWindow : Window, ICredentialPrompt
{
    public MainWindow(MainViewModel viewModel, IDialogService dialogService)
    {
        DataContext = viewModel;
        InitializeComponent();

        if (dialogService is OverlayDialogService overlay)
            overlay.Attach(this);
    }

    // ── ICredentialPrompt: RDP ────────────────────────────────────────────────

    public Task<(string Username, string Password)?> PromptAsync(string targetName)
    {
        var tcs = new TaskCompletionSource<(string, string)?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // Dispatcher.InvokeAsync ставить роботу у чергу UI thread.
        // CredentialDialog.ShowDialog() — WPF модальний діалог:
        //   - має власний message pump → UI thread не "замерзає"
        //   - вікно залишається живим і відповідає
        //   - Owner = this → діалог центрується над MainWindow
        // Це той самий підхід що і ZabbixTokenDialog — перевірений, працює.
        Dispatcher.InvokeAsync(() =>
        {
            var dialog = new CredentialDialog
            {
                Owner = this,
                Title = $"RDP — {targetName}"
            };

            bool? result = dialog.ShowDialog();

            tcs.SetResult(result == true
                ? (dialog.Username, dialog.Password)
                : null);
        });

        return tcs.Task;
    }

    // ── ICredentialPrompt: Zabbix ─────────────────────────────────────────────

    public Task<string?> PromptZabbixTokenAsync()
    {
        var tcs = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.InvokeAsync(() =>
        {
            var dialog = new ZabbixTokenDialog { Owner = this };
            bool? result = dialog.ShowDialog();

            tcs.SetResult(result == true && !string.IsNullOrWhiteSpace(dialog.Token)
                ? dialog.Token
                : null);
        });

        return tcs.Task;
    }

    // ── Overlay dialog ────────────────────────────────────────────────────────

    internal void ShowDialog(
        string title, string body, string confirmLabel,
        TaskCompletionSource<bool> tcs)
    {
        DialogTitle.Text         = title;
        DialogBody.Text          = body;
        DialogConfirmLabel.Text  = confirmLabel;
        DialogOverlay.Visibility = Visibility.Visible;

        void OnConfirm(object s, RoutedEventArgs e)
        {
            DialogConfirmButton.Click -= OnConfirm;
            DialogCancelButton.Click  -= OnCancel;
            DialogOverlay.Visibility   = Visibility.Collapsed;
            tcs.TrySetResult(true);
        }

        void OnCancel(object s, RoutedEventArgs e)
        {
            DialogConfirmButton.Click -= OnConfirm;
            DialogCancelButton.Click  -= OnCancel;
            DialogOverlay.Visibility   = Visibility.Collapsed;
            tcs.TrySetResult(false);
        }

        DialogConfirmButton.Click += OnConfirm;
        DialogCancelButton.Click  += OnCancel;
    }
}