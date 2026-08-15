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

            // Кожне реальне відкриття вкладки Logs (View перестворюється заново
            // при навігації, на відміну від singleton LogsViewModel) — повертаємо
            // сортування до "найновіші зверху", навіть якщо раніше юзер клікав
            // на заголовок стовпця і змінив SortDescriptions того самого VM.
            vm.ApplyDefaultSort();

            // Якщо вже підписані на цей самий VM — не підписуємось повторно
            if (ReferenceEquals(_subscribedVm, vm)) return;

            // Відписуємось від попереднього VM якщо є
            if (_subscribedVm is not null && _collectionHandler is not null)
                _subscribedVm.LogEntries.CollectionChanged -= _collectionHandler;

            _subscribedVm = vm;
            _collectionHandler = (_, _) =>
            {
                if (vm.AutoScroll && LogGrid.Items.Count > 0)
                    LogGrid.ScrollIntoView(LogGrid.Items[0]);
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