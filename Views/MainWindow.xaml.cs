using AdminConsole.Services;
using AdminConsole.ViewModels;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;

namespace AdminConsole.Views;

public partial class MainWindow : Window, ICredentialPrompt
{
    
    private TaskbarIcon? _trayIcon;
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
    
    // ── Tray Icon ─────────────────────────────────────────────────────────────

    public void InitTray()
    {
        // Створюємо TaskbarIcon програмно — явно, без крихкого рядкового ключа
        // з Application.Resources. Якщо іконка не знайдена — отримаємо чітке
        // FileNotFoundException замість InvalidCastException без повідомлення.
        var iconUri = new Uri(
            "pack://application:,,,/Resources/icon.ico",
            UriKind.Absolute);

        _trayIcon = new TaskbarIcon
        {
            IconSource  = new System.Windows.Media.Imaging.BitmapImage(iconUri),
            ToolTipText = "Admin Console",
            Visibility  = Visibility.Visible
        };

        var contextMenu = new System.Windows.Controls.ContextMenu();

        var openItem = new System.Windows.Controls.MenuItem
            { Header = "Відкрити Admin Console" };
        openItem.Click += (_, _) => RestoreWindow();
        contextMenu.Items.Add(openItem);

        contextMenu.Items.Add(new System.Windows.Controls.Separator());

        var exitItem = new System.Windows.Controls.MenuItem { Header = "Вийти" };
        exitItem.Click += (_, _) =>
        {
            _isExiting = true;
            _trayIcon?.Dispose();
            System.Windows.Application.Current.Shutdown();
        };
        contextMenu.Items.Add(exitItem);

        _trayIcon.ContextMenu           = contextMenu;
        _trayIcon.TrayMouseDoubleClick += (_, _) => RestoreWindow();
    }

    public void UpdateTrayTooltip(string text)
    {
        if (_trayIcon is not null)
            _trayIcon.ToolTipText = text;
    }

    private void RestoreWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        //Нічого не виконується
    }

    private bool _isExiting = false;

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // При виході через трей — не перехоплюємо
        if (_isExiting) return;

        e.Cancel    = true;
        WindowState = WindowState.Normal;
        Hide();

        if (_trayIcon is { IsDisposed: false })
        {
            _trayIcon.ShowBalloonTip(
                "Admin Console",
                "Програма згорнута у трей. Двічі клікни для відкриття.",
                BalloonIcon.Info);
        }
    }
}