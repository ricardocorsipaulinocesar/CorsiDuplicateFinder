using System.Globalization;
using System.Windows.Data;

namespace CorsiDuplicate.UI.Converters;

/// <summary>Row height follows the thumbnail size (square thumbnail plus a little padding)
/// so dragging the thumbnail column wider also grows the row, keeping the thumbnail square
/// instead of getting clipped by a fixed row height.</summary>
public sealed class RowHeightFromThumbConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double thumbWidth ? thumbWidth + 8 : 46.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
