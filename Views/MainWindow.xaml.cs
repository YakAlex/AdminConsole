using AdminConsole.ViewModels;
using System.Windows;

namespace AdminConsole.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        // InitializeComponent parses the XAML and builds the visual tree.
        // This MUST be called before setting DataContext.
        InitializeComponent();

        // Assign the DI-provided ViewModel as DataContext.
        // The View knows nothing about how the ViewModel was constructed.
        DataContext = viewModel;
    }
}