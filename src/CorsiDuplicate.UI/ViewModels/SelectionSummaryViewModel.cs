using CommunityToolkit.Mvvm.ComponentModel;
using CorsiDuplicate.UI.ViewModels;

namespace CorsiDuplicate.UI.ViewModels;

public partial class SelectionSummaryViewModel : ObservableObject
{
    [ObservableProperty]
    private long _totalSelectedBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private int _selectedCount;

    public bool HasSelection => SelectedCount > 0;

    public string TotalSelectedDisplay => MediaItemViewModel.FormatBytes(TotalSelectedBytes);

    partial void OnTotalSelectedBytesChanged(long value) => OnPropertyChanged(nameof(TotalSelectedDisplay));

    public void OnSelectionChanged(MediaItemViewModel item)
    {
        if (item.IsSelected)
        {
            SelectedCount++;
            TotalSelectedBytes += item.SizeBytes;
        }
        else
        {
            SelectedCount--;
            TotalSelectedBytes -= item.SizeBytes;
        }
    }

    public void Recompute(IEnumerable<MediaItemViewModel> allItems)
    {
        var selected = allItems.Where(i => i.IsSelected).ToList();
        SelectedCount = selected.Count;
        TotalSelectedBytes = selected.Sum(i => i.SizeBytes);
    }

    public void Reset()
    {
        SelectedCount = 0;
        TotalSelectedBytes = 0;
    }
}
