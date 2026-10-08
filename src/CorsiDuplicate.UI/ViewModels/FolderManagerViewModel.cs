using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CorsiDuplicate.Core.Models;
using CorsiDuplicate.Infrastructure.Logging;
using CorsiDuplicate.Infrastructure.Persistence;
using CorsiDuplicate.Infrastructure.Scanning;

namespace CorsiDuplicate.UI.ViewModels;

public partial class FolderManagerViewModel : ObservableObject
{
    private readonly ManagedFolderStore _store = new();

    public ObservableCollection<ManagedFolderViewModel> Folders { get; } = new();

    /// <summary>Whether the full-screen "Managed folders" overlay is currently open.</summary>
    [ObservableProperty]
    private bool _isManageFoldersOpen;

    private readonly AppSettingsStore _settingsStore = new();

    // Column keys used by the panel's sortable headers.
    private static readonly Dictionary<string, string> SortProperties = new()
    {
        ["Path"] = nameof(ManagedFolderViewModel.Path),
        ["LastModified"] = nameof(ManagedFolderViewModel.LastModifiedSortKey),
        ["LastScanned"] = nameof(ManagedFolderViewModel.LastScannedSortKey),
        ["Items"] = nameof(ManagedFolderViewModel.ItemCountSortKey),
    };

    private string _sortKey = "LastModified";
    private bool _sortDescending = true;

    public FolderManagerViewModel()
    {
        foreach (var folder in _store.Load())
        {
            AddAndTrack(new ManagedFolderViewModel(folder));
        }

        var settings = _settingsStore.Load();
        if (SortProperties.ContainsKey(settings.FolderSortKey))
        {
            _sortKey = settings.FolderSortKey;
            _sortDescending = settings.FolderSortDescending;
        }
        ApplySort();
    }

    public string PathHeader => HeaderFor("Path", "Folder path");
    public string LastModifiedHeader => HeaderFor("LastModified", "Last modified");
    public string LastScannedHeader => HeaderFor("LastScanned", "Last scanned");
    public string ItemsHeader => HeaderFor("Items", "Items");

    private string HeaderFor(string key, string title) =>
        key == _sortKey ? $"{title} {(_sortDescending ? "▼" : "▲")}" : title;

    /// <summary>Header click: same column flips direction; a new column starts with
    /// newest/largest first (A→Z for the path).</summary>
    [RelayCommand]
    private void SortBy(string? key)
    {
        if (key is null || !SortProperties.ContainsKey(key))
        {
            return;
        }

        if (key == _sortKey)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortKey = key;
            _sortDescending = key != "Path";
        }

        ApplySort();

        var settings = _settingsStore.Load();
        settings.FolderSortKey = _sortKey;
        settings.FolderSortDescending = _sortDescending;
        _settingsStore.Save(settings);
    }

    private void ApplySort()
    {
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(Folders);
        var property = SortProperties[_sortKey];
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(property,
            _sortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending));
        view.LiveSortingProperties.Clear();
        view.LiveSortingProperties.Add(property);
        view.IsLiveSorting = true;

        OnPropertyChanged(nameof(PathHeader));
        OnPropertyChanged(nameof(LastModifiedHeader));
        OnPropertyChanged(nameof(LastScannedHeader));
        OnPropertyChanged(nameof(ItemsHeader));
    }

    partial void OnIsManageFoldersOpenChanged(bool value)
    {
        if (value)
        {
            _ = RefreshLastModifiedAsync(Folders.ToList());
        }
    }

    private async Task RefreshLastModifiedAsync(IReadOnlyList<ManagedFolderViewModel> folders)
    {
        var pending = folders.Where(f => !f.IsCalculatingModified).ToList();
        foreach (var folder in pending)
        {
            folder.IsCalculatingModified = true;
        }

        var changed = false;
        foreach (var folder in pending)
        {
            try
            {
                var lastChange = await Task.Run(() => FolderChangeDetector.GetLastChangeUtc(folder.Path));
                if (folder.Model.LastModifiedUtc != lastChange)
                {
                    folder.Model.LastModifiedUtc = lastChange;
                    changed = true;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(nameof(FolderManagerViewModel), nameof(RefreshLastModifiedAsync),
                    $"Could not read last change time for '{folder.Path}'.", ex);
            }
            finally
            {
                folder.IsCalculatingModified = false;
                folder.RefreshFromModel();
            }
        }

        if (changed)
        {
            Persist();
        }
    }

    private void AddAndTrack(ManagedFolderViewModel vm)
    {
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ManagedFolderViewModel.IsEnabled))
            {
                Persist();
            }
        };
        Folders.Add(vm);
    }

    public IEnumerable<ManagedFolderViewModel> EnabledFolders => Folders.Where(f => f.IsEnabled);

    /// <summary>Raised after a folder is removed from the managed list, so anything holding results/cache for that path can drop them too.</summary>
    public event Action<string>? FolderRemoved;

    [RelayCommand]
    private void ToggleManageFolders() => IsManageFoldersOpen = !IsManageFoldersOpen;

    [RelayCommand]
    private void CloseManageFolders() => IsManageFoldersOpen = false;

    public bool AddFolder(string path)
    {
        if (Folders.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var model = new ManagedFolder { Path = path };
        AddAndTrack(new ManagedFolderViewModel(model));
        Persist();
        return true;
    }

    // "Add all subfolders": pick a parent folder, then choose which of its subfolders become
    // separate entries in the list (each gets its own results and "Last modified").
    [ObservableProperty]
    private bool _isSubfolderPickerOpen;

    [ObservableProperty]
    private string _subfolderRoot = "";

    [ObservableProperty]
    private bool _includeDeeperLevels;

    public ObservableCollection<SubfolderCandidate> SubfolderCandidates { get; } = new();

    public int SubfoldersToAdd => SubfolderCandidates.Count(c => c.IsChecked && !c.IsAlreadyAdded);
    public string SubfolderSummary => $"Found {SubfolderCandidates.Count} subfolder(s). Each one becomes its own folder in the list.";
    public string AddSubfoldersLabel => SubfoldersToAdd == 1 ? "Add 1 folder" : $"Add {SubfoldersToAdd} folders";

    /// <summary>Raised with a short status line after subfolders are added.</summary>
    public event Action<string>? StatusChanged;

    [RelayCommand]
    private void AddSubfolders()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select the parent folder" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        SubfolderRoot = dialog.FolderName;
        LoadSubfolderCandidates();
        IsSubfolderPickerOpen = true;
    }

    partial void OnIncludeDeeperLevelsChanged(bool value)
    {
        if (IsSubfolderPickerOpen)
        {
            LoadSubfolderCandidates();
        }
    }

    private void LoadSubfolderCandidates()
    {
        SubfolderCandidates.Clear();
        IEnumerable<string> paths;
        try
        {
            paths = Directory.EnumerateDirectories(SubfolderRoot, "*", new EnumerationOptions
            {
                RecurseSubdirectories = IncludeDeeperLevels,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            }).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.Info(nameof(FolderManagerViewModel), nameof(LoadSubfolderCandidates), $"Could not list '{SubfolderRoot}': {ex.Message}");
            paths = Array.Empty<string>();
        }

        foreach (var path in paths)
        {
            var already = Folders.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
            var candidate = new SubfolderCandidate(path, Path.GetRelativePath(SubfolderRoot, path), already);
            candidate.PropertyChanged += (_, _) => RefreshSubfolderCounts();
            SubfolderCandidates.Add(candidate);
        }
        RefreshSubfolderCounts();
    }

    private void RefreshSubfolderCounts()
    {
        OnPropertyChanged(nameof(SubfoldersToAdd));
        OnPropertyChanged(nameof(SubfolderSummary));
        OnPropertyChanged(nameof(AddSubfoldersLabel));
        OnPropertyChanged(nameof(AllSubfoldersChecked));
    }

    public bool AllSubfoldersChecked
    {
        get => SubfolderCandidates.Where(c => !c.IsAlreadyAdded).All(c => c.IsChecked);
        set
        {
            foreach (var c in SubfolderCandidates.Where(c => !c.IsAlreadyAdded))
            {
                c.IsChecked = value;
            }
        }
    }

    [RelayCommand]
    private void ConfirmSubfolders()
    {
        var added = SubfolderCandidates.Where(c => c.IsChecked && !c.IsAlreadyAdded).Count(c => AddFolder(c.Path));
        IsSubfolderPickerOpen = false;
        SubfolderCandidates.Clear();
        StatusChanged?.Invoke(added == 0 ? "No new subfolders were added." : $"Added {added} subfolder(s) of {SubfolderRoot}.");
    }

    [RelayCommand]
    private void CancelSubfolders()
    {
        IsSubfolderPickerOpen = false;
        SubfolderCandidates.Clear();
    }

    [RelayCommand]
    private void RemoveFolder(ManagedFolderViewModel? folder)
    {
        if (folder is null)
        {
            return;
        }

        Folders.Remove(folder);
        Persist();
        FolderRemoved?.Invoke(folder.Path);
    }

    public void RecordScanResult(string path, int itemCount)
    {
        var vm = Folders.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        if (vm is null)
        {
            return;
        }

        vm.Model.LastScannedUtc = DateTime.UtcNow;
        vm.Model.ItemCount = itemCount;
        vm.RefreshFromModel();
        Persist();
        _ = RefreshLastModifiedAsync(new[] { vm });
    }

    public void Persist() => _store.Save(Folders.Select(f => f.Model));
}
