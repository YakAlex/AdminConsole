using AdminConsole.ViewModels;
using System.Collections.Specialized;
using System.Windows.Controls;

namespace AdminConsole.Views;

public partial class LogsView : UserControl
{
    // Зберігаємо посилання на обробник щоб відписатись у Unloaded
    private NotifyCollectionChangedEventHandler? _collectionHandler;
    private LogsViewModel? _subscribedVm;

    public LogsView()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            if (DataContext is not LogsViewModel vm) return;

            // Якщо вже підписані на цей самий VM — не підписуємось повторно
            if (ReferenceEquals(_subscribedVm, vm)) return;

            // Відписуємось від попереднього VM якщо є
            if (_subscribedVm is not null && _collectionHandler is not null)
                _subscribedVm.LogEntries.CollectionChanged -= _collectionHandler;

            _subscribedVm = vm;
            _collectionHandler = (_, _) =>
            {
                if (vm.AutoScroll && LogGrid.Items.Count > 0)
                    LogGrid.ScrollIntoView(LogGrid.Items[^1]);
            };

            vm.LogEntries.CollectionChanged += _collectionHandler;
        };

        Unloaded += (_, _) =>
        {
            // Відписуємось при видаленні з visual tree
            if (_subscribedVm is not null && _collectionHandler is not null)
            {
                _subscribedVm.LogEntries.CollectionChanged -= _collectionHandler;
                _collectionHandler = null;
                _subscribedVm      = null;
            }
        };
    }
}