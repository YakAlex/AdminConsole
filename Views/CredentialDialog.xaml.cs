using System.Windows;
using System.Windows.Input;

namespace AdminConsole.Views;

public partial class CredentialDialog : Window
{
    public string Username { get; private set; } = string.Empty;
    public string Password { get; private set; } = string.Empty;

    public CredentialDialog()
    {
        InitializeComponent();
        // Фокус на поле логіну одразу після відкриття
        Loaded += (_, _) => UsernameBox.Focus();
    }

    // ── Обробники кнопок ─────────────────────────────────────────────────────

    private void OkBtn_Click(object sender, RoutedEventArgs e)
        => TryConfirm();

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;

    // ── Enter у полях переводить фокус або підтверджує ───────────────────────

    private void UsernameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            PasswordBox.Focus();
    }

    private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            TryConfirm();
    }

    // ── Валідація і закриття ──────────────────────────────────────────────────

    private void TryConfirm()
    {
        ErrorText.Visibility = Visibility.Collapsed;

        if (string.IsNullOrWhiteSpace(UsernameBox.Text))
        {
            ErrorText.Text       = "Логін не може бути порожнім.";
            ErrorText.Visibility = Visibility.Visible;
            UsernameBox.Focus();
            return;
        }

        if (PasswordBox.Password.Length == 0)
        {
            ErrorText.Text       = "Пароль не може бути порожнім.";
            ErrorText.Visibility = Visibility.Visible;
            PasswordBox.Focus();
            return;
        }

        Username     = UsernameBox.Text.Trim();
        Password     = PasswordBox.Password;
        DialogResult = true;
    }
}