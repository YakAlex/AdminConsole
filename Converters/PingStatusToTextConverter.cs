using AdminConsole.Core.Models;
using System.Globalization;
using System.Windows.Data;

namespace AdminConsole.Converters;

/// <summary>
/// Converts PingStatus to a human-readable display string.
/// </summary>
[ValueConversion(typeof(PingStatus), typeof(string))]
public sealed class PingStatusToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is PingStatus status
            ? status switch
            {
                PingStatus.Online   => "Online",
                PingStatus.Offline  => "Offline",
                PingStatus.Checking => "Checking…",
                _                   => "Unknown"
            }
            : "Unknown";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}