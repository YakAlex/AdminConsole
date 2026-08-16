using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AdminConsole.Views;

public partial class OverviewView : UserControl
{
    public OverviewView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Прокидає колесо миші від внутрішнього ScrollViewer картки (feed-зона,
    /// top-5 список) до зовнішнього ScrollViewer сторінки. Без цього WPF
    /// за замовчуванням "з'їдає" MouseWheel у внутрішньому ScrollViewer навіть
    /// тоді, коли в ньому нічого прокручувати — а оскільки картки займають
    /// майже весь екран, курсор майже завжди опиняється над однією з них,
    /// тож прокрутка сторінки колесиком здавалась "зламаною".
    /// </summary>
    private void FeedScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        if (sender is not ScrollViewer { Parent: UIElement parent }) return;

        e.Handled = true;

        var bubbled = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent
        };
        parent.RaiseEvent(bubbled);
    }
}