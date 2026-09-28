using System.Globalization;
using System.Windows.Data;

namespace CorsiDuplicate.UI.Converters;

/// <summary>Total width of the thumbnail cell's filmstrip. With one thumbnail this is just the
/// square thumbnail column width (unchanged from before). With more than one, the cell instead
/// grows sideways, but each additional frame adds progressively less width than the first —
/// a flat per-frame multiplier made a 6-thumbnail row nearly 4x the column width, which read as
/// oversized. Diminishing the per-frame share as the count grows keeps the row compact even at
/// the maximum thumbnail count while each frame stays wide enough to read. Recalculates live off
/// Thumbnails.Count, so it reflows immediately when the "Thumbnails per video" setting changes.</summary>
public sealed class FilmstripWidthConverter : IMultiValueConverter
{
    private const double FirstFrameAspect = 1.15;
    private const double ExtraFrameAspect = 0.5;

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is not [double thumbHeight, int count] || thumbHeight <= 0)
        {
            return values.Length > 0 && values[0] is double h ? h : 64.0;
        }

        return count <= 1
            ? thumbHeight
            : thumbHeight * (FirstFrameAspect + ExtraFrameAspect * (count - 1));
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
