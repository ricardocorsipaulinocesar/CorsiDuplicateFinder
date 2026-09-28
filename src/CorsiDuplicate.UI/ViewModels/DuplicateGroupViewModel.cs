using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CorsiDuplicate.Core.Grouping;
using CorsiDuplicate.Core.Models;

namespace CorsiDuplicate.UI.ViewModels;

public partial class DuplicateGroupViewModel : ObservableObject
{
    // A small, distinguishable palette cycled across exact-hash (byte-identical)
    // sub-clusters within a group, so a viewer can see at a glance which rows are
    // truly identical copies of each other vs. merely similar.
    private static readonly string[] HashColorPalette =
    {
        "#FDE8D2", "#DCEFFB", "#E3F3DC", "#F3E0F7", "#FFF3C4", "#E0E7FF"
    };

    public DuplicateGroup Model { get; }
    public string FolderPath { get; }
    public ObservableCollection<MediaItemViewModel> Items { get; } = new();

    public int SetNumber { get; set; }

    // Overrides SetLabel for the synthetic, set-ignoring view built by the "Thumb"
    // similarity sort — that view flattens every item of a folder into one list ordered
    // purely by thumbnail similarity, so "Set N" would be misleading for it.
    public string? CustomLabel { get; set; }
    public string SetLabel => CustomLabel ?? $"Set {SetNumber}";

    // Back-reference to the folder this set belongs to, set once by MainViewModel when
    // the set is created — lets the "Thumb" header (bound to a single set's DataContext)
    // reach the folder-wide sort state and trigger the folder-wide reorder command.
    public FolderResultsViewModel? Owner { get; set; }

    // The best-quality item's thumbnail stands in for the whole set when sorting sets by
    // visual similarity, since it's the copy the user is most likely to keep and judge by.
    public string? ReferenceThumbnailPath =>
        Items.OrderByDescending(i => QualityScorer.Score(i.Model)).FirstOrDefault()?.ThumbnailPath;

    public DuplicateGroupViewModel(DuplicateGroup model, string folderPath, IEnumerable<MediaItemViewModel> items)
    {
        Model = model;
        FolderPath = folderPath;
        foreach (var item in items)
        {
            Items.Add(item);
        }

        AssignHashGroupColors();
    }

    /// <summary>
    /// Colors every item that shares an exact SHA-256 (byte-identical) match with at
    /// least one sibling in this group, cycling through a small palette per distinct
    /// hash bucket. Singleton hashes (no identical sibling) get no highlight.
    /// </summary>
    private void AssignHashGroupColors()
    {
        foreach (var item in Items)
        {
            item.HashGroupColor = null;
        }

        var buckets = Items.GroupBy(i => i.Model.Sha256).Where(g => g.Count() > 1).ToList();
        for (var i = 0; i < buckets.Count; i++)
        {
            var color = HashColorPalette[i % HashColorPalette.Length];
            foreach (var item in buckets[i])
            {
                item.HashGroupColor = color;
            }
        }
    }

    public string SetMeta
    {
        get
        {
            var best = Items.OrderByDescending(i => QualityScorer.Score(i.Model)).FirstOrDefault();
            return best is null
                ? $"{Items.Count} items"
                : $"Best copy: {best.FileName} · {best.ResolutionDisplay} · {best.SizeDisplay}";
        }
    }

    [RelayCommand]
    private void SelectHighQuality() => ApplyQualityFilter(keepBest: true);

    [RelayCommand]
    private void SelectLowQuality() => ApplyQualityFilter(keepBest: false);

    private void ApplyQualityFilter(bool keepBest)
    {
        if (Items.Count == 0)
        {
            return;
        }

        var ordered = Items.OrderByDescending(i => QualityScorer.Score(i.Model)).ToList();
        var toKeep = keepBest ? ordered.First() : ordered.Last();

        foreach (var item in Items)
        {
            item.IsSelected = !ReferenceEquals(item, toKeep);
        }
    }
}
