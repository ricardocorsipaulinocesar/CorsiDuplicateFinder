using System.Globalization;
using System.Windows.Data;

namespace CorsiDuplicate.UI.Converters;

/// <summary>True when every bound value equals the first one (used to highlight the active sort chip).</summary>
public sealed class AllEqualConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length > 1 && values.All(v => Equals(v, values[0]));

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
