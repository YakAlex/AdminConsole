using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AdminConsole.Core.Models;

namespace AdminConsole.Converters;

/// <summary>
/// Перетворює rolling-історію розмірів бекапу (BackupSample.SizeBytes,
/// впорядковані за ObservedAt) у PointCollection для міні-спарклайну
/// (Polyline) у картці Backups на Overview. Нормалізує Y між
/// min/max розміром історії, X — рівномірно по ширині.
///
/// Фіксовані розміри полотна (160×36) підібрані під конкретний
/// XAML-контейнер спарклайну в OverviewView — якщо розмір контейнера
/// зміниться, ConverterParameter "width,height" дозволяє перевизначити
/// без правки коду конвертера.
/// </summary>
public sealed class BackupHistoryToPointsConverter : IValueConverter
{
    public static readonly BackupHistoryToPointsConverter Instance = new();

    private const double DefaultWidth  = 160;
    private const double DefaultHeight = 36;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not IReadOnlyList<BackupSample> samples || samples.Count < 2)
            return new PointCollection();

        var (width, height) = ParseSize(parameter as string);

        var ordered = samples.OrderBy(s => s.ObservedAt).ToList();
        long min   = ordered.Min(s => s.SizeBytes);
        long max   = ordered.Max(s => s.SizeBytes);
        long range = max - min;

        var points = new PointCollection();
        for (int i = 0; i < ordered.Count; i++)
        {
            double x = width * i / (ordered.Count - 1);

            // Якщо всі семпли однакового розміру (range == 0) —
            // рівна лінія посередині, а не ділення на нуль.
            double normalized = range == 0 ? 0.5 : (double)(ordered[i].SizeBytes - min) / range;
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