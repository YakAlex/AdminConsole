using AdminConsole.Core.Models;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace AdminConsole.Converters;

/// <summary>
/// Converts a PingStatus enum value to a SolidColorBrush for the status
/// indicator dot in the DataGrid.
///
/// Online  → Green  (#4CAF50)
/// Offline → Red    (#F44336)
/// Checking→ Amber  (#FFC107)
/// Unknown → Grey   (#607D8B)
/// </summary>
[ValueConversion(typeof(PingStatus), typeof(SolidColorBrush))]
public sealed class PingStatusToColorConverter : IValueConverter
{
    // Brushes are frozen (immutable) so WPF can cache and share them
    // across all rows without per-row allocation.
    private static readonly SolidColorBrush OnlineBrush   =
        Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)));
    private static readonly SolidColorBrush OfflineBrush  =
        Freeze(new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36)));
    private static readonly SolidColorBrush CheckingBrush =
        Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)));
    private static readonly SolidColorBrush UnknownBrush  =
        Freeze(new SolidColorBrush(Color.FromRgb(0x60, 0x7D, 0x8B)));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is PingStatus status
            ? status switch
            {
                PingStatus.Online   => OnlineBrush,
                PingStatus.Offline  => OfflineBrush,
                PingStatus.Checking => CheckingBrush,
                _                   => UnknownBrush
            }
            : UnknownBrush;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}