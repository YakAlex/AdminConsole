using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AdminConsole.Converters;

/// <summary>
/// Перетворює серію відсоткових значень (0–100, напр. погодинний Uptime% за
/// останні 24г) у PointCollection для міні-спарклайну (Polyline) — той самий
/// підхід за духом, що BackupHistoryToPointsConverter, але з іншою семантикою
/// "усі значення однакові": для розміру бекапу це нейтральний випадок
/// (лінія посередині), а для відсотків "усі 100%" — це завжди найкращий
/// стан, тому лінія кладеться зверху, а не в середину.
///
/// ConverterParameter формат: "width,height", напр. "260,40".
/// </summary>
public sealed class PercentSeriesToPointsConverter : IValueConverter
{
    private const double DefaultWidth  = 260;
    private const double DefaultHeight = 40;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not IReadOnlyList<double> series || series.Count < 2)
            return new PointCollection();

        var (width, height) = ParseSize(parameter as string);

        // Фіксована шкала 0–100, а НЕ динамічний min/max серії (як у
        // BackupHistoryToPointsConverter для розміру бекапу). Для відсотків
        // "усі значення однакові" не є нейтральним випадком:
        //   - якщо всі 24 точки == 0% (сервер лежав добу), динамічна шкала
        //     намалювала б лінію ЗВЕРХУ (як 100%) — 0% і 100% виглядали б
        //     однаково;
        //   - коливання 99.0–100.0% не повинні розтягуватись на всю висоту
        //     графіка, ніби це катастрофа — 1% різниці має виглядати як
        //     ~1% висоти, а не 100%.
        // Абсолютне значення тут — саме те, що має сенс показувати.
        var points = new PointCollection();
        for (int i = 0; i < series.Count; i++)
        {
            double x = width * i / (series.Count - 1);

            double normalized = Math.Clamp(series[i], 0.0, 100.0) / 100.0;
            double y = height - normalized * height;

            points.Add(new Point(x, y));
        }

        return points;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static (double Width, double Height) ParseSize(string? param)
    {
        if (string.IsNullOrWhiteSpace(param)) return (DefaultWidth, DefaultHeight);

        var parts = param.Split(',');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var w) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
            return (w, h);

        return (DefaultWidth, DefaultHeight);
    }
}