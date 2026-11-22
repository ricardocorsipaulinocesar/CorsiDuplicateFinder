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
        var vm = new MainViewModel();
        DataContext = vm;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.TaskbarProgress) or nameof(MainViewModel.TaskbarState))
            {
                UpdateTaskbarBadge(vm);
            }
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Controls.TitleBarTheme.Apply(this);
    }

    private int _badgeShown = -1;

    /// <summary>Small number badge on the taskbar icon with the overall percentage (hidden when idle or unmeasurable).</summary>
    private void UpdateTaskbarBadge(MainViewModel vm)
    {
        int percent = vm.TaskbarState == System.Windows.Shell.TaskbarItemProgressState.Normal
            ? (int)Math.Round(vm.TaskbarProgress * 100)
            : -1;
        if (percent == _badgeShown || TaskbarItemInfo is null)
        {
            return;
        }

        _badgeShown = percent;
        if (percent < 0)
        {
            TaskbarItemInfo.Overlay = null;
            return;
        }

        var visual = new System.Windows.Controls.Grid { Width = 32, Height = 32 };
        visual.Children.Add(new System.Windows.Shapes.Ellipse { Fill = new SolidColorBrush(Color.FromRgb(0x66, 0x90, 0xF2)) });
        visual.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = percent.ToString(),
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            FontSize = percent >= 100 ? 13 : 17,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        visual.Measure(new Size(32, 32));
        visual.Arrange(new Rect(0, 0, 32, 32));
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        TaskbarItemInfo.Overlay = bitmap;
    }

    // Thumbnail images are only decoded for rows that are actually realized on screen.
    private void ThumbnailCell_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MediaItemViewModel item })
        {
            item.EnsureThumbnailsDecoded();
        }
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

    /// <summary>
    /// Handles every button in a folder's "Filter all results by:" row (Thumb, File,
    /// Size, Length, Resolution, Bit rate, Audio, Match, Hash — see MainWindow.xaml).
    /// Unlike the old per-set "Thumb" column header, this row lives directly inside the
    /// folder's own DataTemplate, so DataContext here is already the correct
    /// FolderResultsViewModel with no DataGridColumn quirk to work around. Each button's
    /// Tag carries which field it filters by.
    /// </summary>
    private void FolderFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FolderResultsViewModel folder, Tag: string filterKey } &&
            DataContext is MainViewModel vm)
        {
            vm.ToggleFolderFilterCommand.Execute(new FolderFilterRequest(folder, filterKey));
        }
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
