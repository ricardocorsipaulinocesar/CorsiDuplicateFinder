using CommunityToolkit.Mvvm.ComponentModel;

namespace CorsiDuplicate.UI.ViewModels;

/// <summary>One row of the "Add all subfolders" picker. Folders already in the managed list
/// are shown checked and disabled.</summary>
public partial class SubfolderCandidate : ObservableObject
{
    public SubfolderCandidate(string path, string displayName, bool isAlreadyAdded)
    {
        Path = path;
        DisplayName = displayName;
        IsAlreadyAdded = isAlreadyAdded;
        _isChecked = true;
    }

    public string Path { get; }
    public string DisplayName { get; }
    public bool IsAlreadyAdded { get; }
    public bool CanToggle => !IsAlreadyAdded;

    [ObservableProperty]
    private bool _isChecked;
}
