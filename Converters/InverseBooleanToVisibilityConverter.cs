using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AdminConsole.Converters;

/// <summary>
/// true  → Collapsed
/// false → Visible
///
/// Використовується для приховування основного контенту (DataGrid сесій RDP,
/// список проблем Zabbix), коли VM.IsMonitoringDisabled = true — тобто це
/// протилежність стандартному BooleanToVisibilityConverter.
/// </summary>
[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public static readonly InverseBooleanToVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Collapsed;
}
