using System.Windows;
using System.Windows.Input;

namespace AdminConsole.Views;

public partial class ZabbixTokenDialog : Window
{
    public string Token { get; private set; } = string.Empty;

    public ZabbixTokenDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => TokenBox.Focus();
    }

    private void OkBtn_Click(object sender, RoutedEventArgs e)
    {
        string token = TokenBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            ErrorText.Text       = "Токен не може бути порожнім.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        Token        = token;
        DialogResult = true;
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;

    private void TokenBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OkBtn_Click(sender, e);
    }
}