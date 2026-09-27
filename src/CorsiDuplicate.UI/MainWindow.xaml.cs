using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CorsiDuplicate.UI.ViewModels;

namespace CorsiDuplicate.UI;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }

    private void ThumbnailResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            var newWidth = vm.ThumbnailColumnWidth + e.HorizontalChange;
            vm.ThumbnailColumnWidth = Math.Clamp(newWidth, 48, 400);
        }
    }

    private void ThumbnailBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MediaItemViewModel item } && item.IsVideo)
        {
            item.PlayCommand.Execute(null);
        }
    }

    private void FolderHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FolderResultsViewModel folder })
        {
            folder.ToggleExpandedCommand.Execute(null);
        }
    }

    /// <summary>
    /// Each duplicate group's DataGrid has its own internal ScrollViewer, which always
    /// marks a mouse-wheel event as handled — even when the grid has nothing left to
    /// scroll — so the event never bubbles up to the results area's outer ScrollViewer.
    /// Forwarding it manually is what makes scrolling over a group's rows move the page
    /// instead of appearing to do nothing.
    /// </summary>
    private void ResultsDataGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject element)
        {
            return;
        }

        e.Handled = true;
        var forwarded = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent
        };

        var parent = VisualTreeHelper.GetParent(element);
        (parent as UIElement)?.RaiseEvent(forwarded);
    }

    private void ManageFoldersBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.FolderManager.CloseManageFoldersCommand.Execute(null);
        }
    }

    /// <summary>Stops a click on the modal sheet itself from bubbling up to the backdrop and closing the overlay.</summary>
    private void ManageFoldersSheet_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }
}
