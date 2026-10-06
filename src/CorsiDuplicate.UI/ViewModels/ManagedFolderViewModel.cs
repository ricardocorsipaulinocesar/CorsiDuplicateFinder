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

    [ObservableProperty]
    private bool _isCalculatingModified;

    partial void OnIsEnabledChanged(bool value) => Model.IsEnabled = value;

    partial void OnIsScanningChanged(bool value) => OnPropertyChanged(nameof(LastScannedDisplay));

    // Shows the cached value while a refresh runs; "Calculating..." only when nothing is known yet.
    partial void OnIsCalculatingModifiedChanged(bool value) => OnPropertyChanged(nameof(LastModifiedDisplay));

    public string LastScannedDisplay => IsScanning
        ? "Scanning..."
        : Model.LastScannedUtc is { } t
            ? t.ToLocalTime().ToString("MMM d, HH:mm")
            : "Never";

    public string LastModifiedDisplay => Model.LastModifiedUtc is { } t
        ? t.ToLocalTime().ToString("MMM d, yyyy HH:mm")
        : IsCalculatingModified ? "Calculating..." : "—";

    public string LastModifiedRelative => Model.LastModifiedUtc is { } t ? Relative(DateTime.UtcNow - t) : "";

    public bool IsChangedSinceScan =>
        Model.LastModifiedUtc is { } modified && Model.LastScannedUtc is { } scanned && modified > scanned;

    public string ItemCountDisplay => Model.ItemCount > 0 ? $"{Model.ItemCount:N0} files" : "—";

    // Sort keys: unknown dates/counts sort as the oldest/smallest.
    public DateTime LastModifiedSortKey => Model.LastModifiedUtc ?? DateTime.MinValue;
    public DateTime LastScannedSortKey => Model.LastScannedUtc ?? DateTime.MinValue;
    public int ItemCountSortKey => Model.ItemCount;

    public void RefreshFromModel()
    {
        OnPropertyChanged(nameof(LastScannedDisplay));
        OnPropertyChanged(nameof(ItemCountDisplay));
        OnPropertyChanged(nameof(LastModifiedDisplay));
        OnPropertyChanged(nameof(LastModifiedRelative));
        OnPropertyChanged(nameof(IsChangedSinceScan));
        OnPropertyChanged(nameof(LastModifiedSortKey));
        OnPropertyChanged(nameof(LastScannedSortKey));
        OnPropertyChanged(nameof(ItemCountSortKey));
    }

    private static string Relative(TimeSpan age)
    {
        if (age < TimeSpan.Zero) return "just now";
        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes} min ago";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours} h ago";
        if (age.TotalDays < 31) return (int)age.TotalDays == 1 ? "1 day ago" : $"{(int)age.TotalDays} days ago";
        if (age.TotalDays < 365) return $"{(int)(age.TotalDays / 30)} mo ago";
        return $"{(int)(age.TotalDays / 365)} yr ago";
    }
}
