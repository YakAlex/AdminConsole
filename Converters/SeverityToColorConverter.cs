using AdminConsole.Core.Models;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace AdminConsole.Converters;

[ValueConversion(typeof(EventSeverity), typeof(SolidColorBrush))]
public sealed class SeverityToColorConverter : IValueConverter
{
    private static readonly SolidColorBrush CriticalBrush =
        Freeze(new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36))); // Red
    private static readonly SolidColorBrush ErrorBrush =
        Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x57, 0x22))); // Deep Orange
    private static readonly SolidColorBrush WarningBrush =
        Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07))); // Amber
    private static readonly SolidColorBrush InfoBrush =
        Freeze(new SolidColorBrush(Color.FromRgb(0x90, 0xA4, 0xAE))); // Blue Grey

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is EventSeverity s ? s switch
        {
            EventSeverity.Critical    => CriticalBrush,
            EventSeverity.Error       => ErrorBrush,
            EventSeverity.Warning     => WarningBrush,
            _                         => InfoBrush
        } : InfoBrush;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
}