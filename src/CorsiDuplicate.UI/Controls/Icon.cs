using System.Windows;
using System.Windows.Media;

namespace CorsiDuplicate.UI.Controls;

/// <summary>Attached property that gives a Button its icon (a geometry on a 24x24 grid) for the
/// IconTile / IconChip styles.</summary>
public static class Icon
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.RegisterAttached("Data", typeof(Geometry), typeof(Icon), new FrameworkPropertyMetadata(null));

    public static Geometry? GetData(DependencyObject element) => (Geometry?)element.GetValue(DataProperty);

    public static void SetData(DependencyObject element, Geometry? value) => element.SetValue(DataProperty, value);
}
