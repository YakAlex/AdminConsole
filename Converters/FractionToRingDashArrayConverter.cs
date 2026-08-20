using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace AdminConsole.Converters;

/// <summary>
/// Перетворює частку (0.0–1.0, напр. Ping.OnlineCount/TotalCount) у
/// StrokeDashArray для Ellipse, що імітує кільце-прогрес (System Health
/// hero-картка на Overview) — той самий підхід за духом, що
/// BackupHistoryToPointsConverter для спарклайну: конвертер рахує геометрію,
/// XAML лише малює.
///
/// WPF Dash-значення відносні до StrokeThickness (довжина сегмента =
/// значення × StrokeThickness), тому довжину кола треба виразити в
/// "одиницях товщини штриха", а не в пікселях — звідси ParseParam(diameter,strokeThickness).
///
/// ConverterParameter формат: "diameter,strokeThickness", напр. "130,10".
/// </summary>
public sealed class FractionToRingDashArrayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double fraction = value is double d ? Math.Clamp(d, 0.0, 1.0) : 0.0;
        var (diameter, strokeThickness) = ParseParam(parameter as string);

        double radius = (diameter - strokeThickness) / 2.0;
        double circumferenceInStrokeUnits = 2 * Math.PI * radius / strokeThickness;

        double filled = circumferenceInStrokeUnits * fraction;
        double empty  = circumferenceInStrokeUnits - filled;

        // Мінімальний "хвостик" filled-сегмента навіть при fraction == 0,
        // щоб при 0/0 (немає жодного сервера) кільце не малювалось як
        // повністю порожнє коло без жодного видимого штриха.
        if (filled < 0.01) filled = 0.01;

        return new DoubleCollection { filled, empty };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static (double Diameter, double StrokeThickness) ParseParam(string? param)
    {
        if (!string.IsNullOrWhiteSpace(param))
        {
            var parts = param.Split(',');
            if (parts.Length == 2 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var dia) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var th))
                return (dia, th);
        }
        return (130, 10); // дефолт під розмір кільця в OverviewView
    }
}