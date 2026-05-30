using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace AdminConsole.Converters;

/// <summary>
/// Converts a hex color string (e.g. "#FF4CAF50") to a WPF Color struct.
/// Used to drive ProgressBar foreground color from a ViewModel string property.
/// Exposed as a singleton so it can be referenced via x:Static in XAML.
/// </summary>
[ValueConversion(typeof(string), typeof(Color))]
public sealed class StringToColorConverter : IValueConverter
{
    public static readonly StringToColorConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex)
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                return color;
            }
            catch { }
        }
        return Colors.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}