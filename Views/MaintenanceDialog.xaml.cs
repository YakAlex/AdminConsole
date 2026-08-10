using System.Windows;
using AdminConsole.Core.Models;

namespace AdminConsole.Views;

public partial class MaintenanceDialog : Window
{
    public MaintenanceDurationChoice? Choice { get; private set; }

    public MaintenanceDialog(string serverName, string ip)
    {
        InitializeComponent();
        HeaderText.Text = $"Maintenance — {serverName}";
        Title = $"Maintenance — {serverName} ({ip})";
    }

    private void Duration10_Click(object sender, RoutedEventArgs e) => Commit(TimeSpan.FromMinutes(10));
    private void Duration30_Click(object sender, RoutedEventArgs e) => Commit(TimeSpan.FromMinutes(30));
    private void Duration60_Click(object sender, RoutedEventArgs e) => Commit(TimeSpan.FromMinutes(60));
    private void Indefinite_Click(object sender, RoutedEventArgs e) => Commit(null);

    private void Commit(TimeSpan? duration)
    {
        Choice = new MaintenanceDurationChoice
        {
            Duration = duration,
            Comment  = CommentBox.Text
        };
        DialogResult = true;
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}