using AdminConsole.Core.Models;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace AdminConsole.Converters;

/// <summary>
/// Maps ZabbixSeverity enum to a frozen SolidColorBrush.
/// Mirrors Zabbix's own colour scheme so the UI feels familiar.
/// </summary>
[ValueConversion(typeof(ZabbixSeverity), typeof(SolidColorBrush))]
public sealed class ZabbixSeverityToColorConverter : IValueConverter
{
    private static readonly SolidColorBrush DisasterBrush =
        Freeze(new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35))); // Deep Red
    private static readonly SolidColorBrush HighBrush =
        Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x57, 0x22))); // Deep Orange
    private static readonly SolidColorBrush AverageBrush =
        Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00))); // Orange
    private static readonly SolidColorBrush WarningBrush =
        Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00))); // Yellow
    private static readonly SolidColorBrush InfoBrush =
        Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0x97, 0xA7))); // Cyan
    private static readonly SolidColorBrush NotClassifiedBrush =
        Freeze(new SolidColorBrush(Color.FromRgb(0x78, 0x90, 0x9C))); // Blue Grey

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is ZabbixSeverity s ? s switch
        {
            ZabbixSeverity.Disaster      => DisasterBrush,
            ZabbixSeverity.High          => HighBrush,
            ZabbixSeverity.Average       => AverageBrush,
            ZabbixSeverity.Warning       => WarningBrush,
            ZabbixSeverity.Information   => InfoBrush,
            _                            => NotClassifiedBrush
        } : NotClassifiedBrush;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
}