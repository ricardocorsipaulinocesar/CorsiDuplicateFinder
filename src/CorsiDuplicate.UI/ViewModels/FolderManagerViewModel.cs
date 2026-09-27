using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CorsiDuplicate.Core.Models;
using CorsiDuplicate.Infrastructure.Persistence;

namespace CorsiDuplicate.UI.ViewModels;

public partial class FolderManagerViewModel : ObservableObject
{
    private readonly ManagedFolderStore _store = new();

    public ObservableCollection<ManagedFolderViewModel> Folders { get; } = new();

    /// <summary>Whether the full-screen "Managed folders" overlay is currently open.</summary>
    [ObservableProperty]
    private bool _isManageFoldersOpen;

    public FolderManagerViewModel()
    {
        foreach (var folder in _store.Load())
        {
            AddAndTrack(new ManagedFolderViewModel(folder));
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
    }

    public void Persist() => _store.Save(Folders.Select(f => f.Model));
}
