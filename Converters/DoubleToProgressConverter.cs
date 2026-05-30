using System.Globalization;
using System.Windows.Data;

namespace AdminConsole.Converters;

/// <summary>
/// Clamps a double (0–100) to a valid ProgressBar Value.
/// Prevents binding errors if a counter briefly returns a value
/// outside the expected range during startup.
/// </summary>
[ValueConversion(typeof(double), typeof(double))]
public sealed class DoubleToProgressConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double d ? Math.Clamp(d, 0.0, 100.0) : 0.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}