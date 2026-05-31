using AdminConsole.Services;
using AdminConsole.ViewModels;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;

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

        Dispatcher.InvokeAsync(() =>
        {
            var (username, password, ok) = ShowWindowsCredentialDialog(
                targetName,
                "RDP Monitor — облікові дані",
                $"Введи облікові дані для підключення до Terminal Server.\n" +
                $"Формат: DOMAIN\\username або username@domain\n\n" +
                $"Дані будуть збережені у Windows Credential Manager.");

            tcs.SetResult(ok ? (username, password) : null);
        });

        return tcs.Task;
    }

    // ── ICredentialPrompt: Zabbix API токен ───────────────────────────────────

    /// <summary>
    /// Показує простий WPF діалог для введення Zabbix API токену.
    /// Використовуємо власний overlay (той самий механізм що для підтвердження дій)
    /// але з текстовим полем замість кнопок Confirm/Cancel.
    /// </summary>
    public Task<string?> PromptZabbixTokenAsync()
    {
        var tcs = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.InvokeAsync(() =>
        {
            // Створюємо простий діалог з PasswordBox
            var dialog = new ZabbixTokenDialog();
            dialog.Owner = this;
            bool? result = dialog.ShowDialog();

            tcs.SetResult(result == true && !string.IsNullOrWhiteSpace(dialog.Token)
                ? dialog.Token
                : null);
        });

        return tcs.Task;
    }

    // ── Windows Credential Dialog ─────────────────────────────────────────────

    private static (string Username, string Password, bool Ok)
        ShowWindowsCredentialDialog(string target, string caption, string message)
    {
        var usernameBuilder = new StringBuilder(512);
        var passwordBuilder = new StringBuilder(512);
        int maxChars = 512;

        var uiInfo = new CREDUI_INFO
        {
            cbSize         = Marshal.SizeOf<CREDUI_INFO>(),
            hwndParent     = IntPtr.Zero,
            pszCaptionText = caption,
            pszMessageText = message,
            hbmBanner      = IntPtr.Zero
        };

        bool save = false;

        int result = CredUIPromptForCredentials(
            ref uiInfo, target, IntPtr.Zero, 0,
            usernameBuilder, maxChars,
            passwordBuilder, maxChars,
            ref save,
            CREDUI_FLAGS.GENERIC_CREDENTIALS |
            CREDUI_FLAGS.ALWAYS_SHOW_UI      |
            CREDUI_FLAGS.DO_NOT_PERSIST);

        return result == 0
            ? (usernameBuilder.ToString(), passwordBuilder.ToString(), true)
            : (string.Empty, string.Empty, false);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDUI_INFO
    {
        public int    cbSize;
        public IntPtr hwndParent;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszMessageText;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszCaptionText;
        public IntPtr hbmBanner;
    }

    [Flags]
    private enum CREDUI_FLAGS : uint
    {
        GENERIC_CREDENTIALS = 0x0040,
        ALWAYS_SHOW_UI      = 0x1000,
        DO_NOT_PERSIST      = 0x0002
    }

    [DllImport("credui.dll", CharSet = CharSet.Unicode)]
    private static extern int CredUIPromptForCredentials(
        ref CREDUI_INFO pUiInfo, string pszTargetName,
        IntPtr Reserved, int dwAuthError,
        StringBuilder pszUserName, int ulUserNameMaxChars,
        StringBuilder pszPassword, int ulPasswordMaxChars,
        ref bool pfSave, CREDUI_FLAGS dwFlags);

    // ── Overlay dialog (без змін) ─────────────────────────────────────────────

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