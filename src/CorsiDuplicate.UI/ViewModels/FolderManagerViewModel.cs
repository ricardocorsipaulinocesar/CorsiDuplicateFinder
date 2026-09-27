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

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isHidden;

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

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void ToggleHidden()
    {
        IsHidden = !IsHidden;
        if (IsHidden)
        {
            IsExpanded = false;
        }
    }

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
