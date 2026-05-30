using AdminConsole.ViewModels;
using System.Windows.Controls;

namespace AdminConsole.Views;

public partial class LogsView : UserControl
{
    public LogsView()
    {
        InitializeComponent();

        // Wire the DataGrid's ScrollIntoView for auto-scroll.
        // We do this in code-behind because it requires a reference to
        // the named DataGrid control — this is purely a UI behaviour,
        // zero business logic, so it is acceptable in code-behind.
        Loaded += (_, _) =>
        {
            if (DataContext is LogsViewModel vm)
            {
                vm.LogEntries.CollectionChanged += (_, _) =>
                {
                    if (vm.AutoScroll && LogGrid.Items.Count > 0)
                    {
                        LogGrid.ScrollIntoView(LogGrid.Items[^1]);
                    }
                };
            }
        };
    }
}