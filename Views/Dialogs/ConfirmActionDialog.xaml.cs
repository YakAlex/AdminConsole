using MaterialDesignThemes.Wpf;
using System.Windows.Controls;

namespace AdminConsole.Views.Dialogs;

public partial class ConfirmActionDialog : UserControl
{
    public ConfirmActionDialog()
    {
        InitializeComponent();
    }

    // Removed `new` keyword — UserControl.Title does not exist as an
    // accessible member, so hiding it was never valid. This is simply
    // our own string property with no conflict.
    public string DialogTitle
    {
        get => TitleBlock.Text;
        set => TitleBlock.Text = value;
    }

    public string BodyText
    {
        get => BodyBlock.Text;
        set => BodyBlock.Text = value;
    }

    // ConfirmLabel is now a named TextBlock in the Button's logical tree
    // (not inside a ControlTemplate), so InitializeComponent() registers
    // it and this property resolves correctly.
    public string ConfirmText
    {
        get => ConfirmLabel.Text;
        set => ConfirmLabel.Text = value;
    }

    private void ConfirmBtn_Click(object sender, System.Windows.RoutedEventArgs e)
        => DialogHost.CloseDialogCommand.Execute(true, this);

    private void CancelBtn_Click(object sender, System.Windows.RoutedEventArgs e)
        => DialogHost.CloseDialogCommand.Execute(false, this);
}