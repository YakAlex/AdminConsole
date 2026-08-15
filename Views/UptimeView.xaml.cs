using System.Windows.Controls;
using AdminConsole.ViewModels;

namespace AdminConsole.Views;

public partial class UptimeView : UserControl
{
    public UptimeView()
    {
        InitializeComponent();

        // Кожне реальне відкриття вкладки Uptime (View перестворюється
        // заново при навігації, на відміну від singleton UptimeViewModel) —
        // повертаємо сортування до "найновіші зверху за FellAt", навіть
        // якщо раніше юзер клікав на заголовок іншого стовпця і змінив
        // SortDescriptions того самого VM.
        Loaded += (_, _) =>
        {
            if (DataContext is UptimeViewModel vm)
                vm.ApplyDefaultSort();
        };
    }
}