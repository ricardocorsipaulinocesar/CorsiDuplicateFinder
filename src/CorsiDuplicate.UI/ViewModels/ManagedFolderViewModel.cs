using CommunityToolkit.Mvvm.ComponentModel;
using CorsiDuplicate.Core.Models;

namespace CorsiDuplicate.UI.ViewModels;

public partial class ManagedFolderViewModel : ObservableObject
{
    public ManagedFolder Model { get; }

    public ManagedFolderViewModel(ManagedFolder model)
    {
        Model = model;
        _isEnabled = model.IsEnabled;
    }

    public string Path => Model.Path;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _isScanning;

    partial void OnIsEnabledChanged(bool value) => Model.IsEnabled = value;

    partial void OnIsScanningChanged(bool value) => OnPropertyChanged(nameof(LastScannedDisplay));

    public string LastScannedDisplay => IsScanning
        ? "Scanning..."
        : Model.LastScannedUtc is { } t
            ? t.ToLocalTime().ToString("MMM d, HH:mm")
            : "Never";

    public string ItemCountDisplay => Model.ItemCount > 0 ? $"{Model.ItemCount:N0} files" : "—";

    public void RefreshFromModel()
    {
        OnPropertyChanged(nameof(LastScannedDisplay));
        OnPropertyChanged(nameof(ItemCountDisplay));
    }
}
