using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AdminConsole.Converters;

/// <summary>
/// true  → Visible
/// false → Collapsed
///
/// Прямий (не інвертований) варіант — використовується для показу
/// заглушки "Моніторинг вимкнено" саме тоді, коли IsMonitoringDisabled = true.
/// Узгоджено зі стилем проєкту: доступ через x:Static Instance, а не
/// через StaticResource/x:Key.
///
/// ВАЖЛИВО: клас навмисно НЕ називається "BooleanToVisibilityConverter" —
/// таку назву вже має вбудований System.Windows.Controls.BooleanToVisibilityConverter
/// (і навіть зареєстрований під тим самим ключем у MainWindow.xaml Resources).
/// Однакова коротка назва в різних namespace ризикує спричинити CS0104
/// (ambiguous reference), якщо колись в одному файлі опиняться одночасно
/// "using System.Windows.Controls;" та "using AdminConsole.Converters;".
/// </summary>
[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class DirectBooleanToVisibilityConverter : IValueConverter
{
    public static readonly DirectBooleanToVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}
