using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CorsiDuplicate.Core.Models;
using CorsiDuplicate.Infrastructure.Media;

namespace CorsiDuplicate.UI.ViewModels;

public sealed record ContainmentBar(System.Windows.Thickness Margin, double Width, string Color);

public partial class MediaItemViewModel : ObservableObject
{
    private static readonly ThumbnailService ThumbnailService = new();

    // How many thumbnails a video row displays — shared by every item so they all follow
    // the same setting, kept in sync by MainViewModel from the persisted app setting.
    // Photos always show exactly one, regardless of this value.
    public static int ThumbnailsPerVideo { get; set; } = 1;

    public MediaItem Model { get; }
    public string FolderPath { get; }

    public event Action<MediaItemViewModel>? SelectionChanged;
    public event Action<MediaItemViewModel>? PlayRequested;

    public MediaItemViewModel(MediaItem model, string folderPath)
    {
        Model = model;
        FolderPath = folderPath;
        ThumbnailsReadyTask = LoadThumbnailsAsync();
    }

    // The in-flight (or already-finished) thumbnail generation for this item. A caller that
    // needs to know when generation has genuinely finished — not just when the scan/grouping
    // step handed back results — awaits this instead of assuming thumbnails are ready the
    // moment the item appears, since loading/generating them is a separate background step.
    public Task ThumbnailsReadyTask { get; private set; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string? _hashGroupColor;

    // Filled only while the folder's "Contained" view is active: where this clip sits inside
    // its source video (or, for the source itself, a short description), plus the bar
    // geometry for the position indicator, in pixels of ContainmentBarTrackWidth.
    public const double ContainmentBarTrackWidth = 220;

    public const string ClipColor = "#B89AF5";
    public const string CompilationColor = "#E8B34B";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContainmentNote))]
    private string? _containmentNote;

    public bool HasContainmentNote => ContainmentNote is not null;

    [ObservableProperty]
    private string _containmentColor = ClipColor;

    [ObservableProperty]
    private bool _hasContainmentBars;

    // One highlighted stretch per shared segment, positioned along a track that represents
    // the whole length of the video the positions refer to.
    public ObservableCollection<ContainmentBar> ContainmentBars { get; } = new();

    // Role pill above the file name ("Main video", "Excerpt of the main", ...), and whether the
    // row sits under the set's main video (indented, with a rule on its left).
    public const string MainRoleColor = "#7F77DD";
    public const string ExcerptRoleColor = "#1D9E75";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContainmentRole))]
    private string? _containmentRole;

    public bool HasContainmentRole => ContainmentRole is not null;

    [ObservableProperty]
    private string _containmentRoleColor = MainRoleColor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContainmentIndent), nameof(ContainmentRule))]
    private bool _isContainmentChild;

    public System.Windows.Thickness ContainmentIndent => IsContainmentChild ? new(18, 0, 0, 0) : new(0);
    public System.Windows.Thickness ContainmentRule => IsContainmentChild ? new(3, 0, 0, 0) : new(0);

    // Colour of the rule at the left of a child row (a compilation's source videos each get their own).
    [ObservableProperty]
    private string _containmentAccent = "#454C5B";

    public void SetContainmentAccent(string color) => ContainmentAccent = color;

    public void SetContainment(string role, string roleColor, bool isChild, string note, string color,
        IEnumerable<(int Start, int End)> stretches, int totalSeconds)
    {
        ContainmentRole = role;
        ContainmentRoleColor = roleColor;
        IsContainmentChild = isChild;
        ContainmentNote = note;
        ContainmentColor = color;
        ContainmentBars.Clear();
        if (totalSeconds > 0)
        {
            foreach (var (start, end) in stretches)
            {
                var offset = ContainmentBarTrackWidth * start / totalSeconds;
                var width = Math.Max(3, ContainmentBarTrackWidth * (end - start) / totalSeconds);
                ContainmentBars.Add(new ContainmentBar(new System.Windows.Thickness(offset, 0, 0, 0), width, color));
            }
        }
        HasContainmentBars = ContainmentBars.Count > 0;
    }

    /// <summary>Replaces the bar with one coloured stretch per entry (a compilation shows a colour
    /// per source video, and red for the parts that exist nowhere else).</summary>
    public void SetColoredBars(IEnumerable<(int Start, int End, string Color)> stretches, int totalSeconds)
    {
        ContainmentBars.Clear();
        if (totalSeconds > 0)
        {
            foreach (var (start, end, color) in stretches)
            {
                var offset = ContainmentBarTrackWidth * start / totalSeconds;
                var width = Math.Max(3, ContainmentBarTrackWidth * (end - start) / totalSeconds);
                ContainmentBars.Add(new ContainmentBar(new System.Windows.Thickness(offset, 0, 0, 0), width, color));
            }
        }
        HasContainmentBars = ContainmentBars.Count > 0;
    }

    // Extra legend line that depends on what is checked: for a compilation marked for deletion,
    // what is lost and what stays on the computer; for its source videos, what they keep.
    public const string NoteGoodColor = "#3FCA80";
    public const string NoteBadColor = "#EA6F64";

    private readonly Dictionary<string, (string Text, string Color)> _selectionNotes = new();
    private readonly List<Action> _selectionRefreshers = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectionNote))]
    private string? _selectionNote;

    [ObservableProperty]
    private string _selectionNoteColor = NoteGoodColor;

    public bool HasSelectionNote => SelectionNote is not null;

    /// <summary>Sets (or, with null, removes) the line contributed by one compilation set.</summary>
    public void SetSelectionNote(string key, string? text, string color = NoteGoodColor)
    {
        if (text is null)
        {
            _selectionNotes.Remove(key);
        }
        else
        {
            _selectionNotes[key] = (text, color);
        }

        SelectionNote = _selectionNotes.Count == 0 ? null : string.Join("\n", _selectionNotes.Values.Select(n => n.Text));
        SelectionNoteColor = _selectionNotes.Values.Any(n => n.Color == NoteBadColor) ? NoteBadColor : NoteGoodColor;
    }

    /// <summary>Runs when this video is checked or unchecked, to refresh the sets it takes part in.</summary>
    public void AddSelectionRefresher(Action refresh) => _selectionRefreshers.Add(refresh);

    /// <summary>Adds a legend line for a second Set this video belongs to.</summary>
    public void AppendContainmentNote(string note) =>
        ContainmentNote = ContainmentNote is null ? note : $"{ContainmentNote}\n{note}";

    public void ClearContainment()
    {
        ContainmentNote = null;
        ContainmentRole = null;
        IsContainmentChild = false;
        ContainmentColor = ClipColor;
        ContainmentBars.Clear();
        HasContainmentBars = false;
        ContainmentAccent = "#454C5B";
        _selectionNotes.Clear();
        _selectionRefreshers.Clear();
        SelectionNote = null;
    }

    public ObservableCollection<BitmapImage> Thumbnails { get; } = new();

    partial void OnIsSelectedChanged(bool value)
    {
        SelectionChanged?.Invoke(this);
        foreach (var refresh in _selectionRefreshers.ToList())
        {
            refresh();
        }
    }

    /// <summary>Re-fetches/regenerates this item's thumbnails — called after the user
    /// changes <see cref="ThumbnailsPerVideo"/> so already-displayed rows update too.</summary>
    public void ReloadThumbnails() => ThumbnailsReadyTask = LoadThumbnailsAsync();

    // The thumbnail files are generated for every item (the scan waits for that), but the
    // images are only decoded into memory once a row is shown — see EnsureThumbnailsDecoded.
    private IReadOnlyList<string> _thumbnailPaths = Array.Empty<string>();
    private bool _decodeRequested;
    private bool _firstLoadDone;

    private async Task LoadThumbnailsAsync()
    {
        var count = IsVideo ? ThumbnailsPerVideo : 1;
        _thumbnailPaths = await ThumbnailService.EnsureThumbnailsAsync(Model, count);

        // The first load is decoded by EnsureThumbnailsDecoded (which waits for it); a reload
        // after the thumbnails-per-video setting changed refreshes rows already on screen.
        var isReload = _firstLoadDone;
        _firstLoadDone = true;
        if (isReload && _decodeRequested)
        {
            await DecodeThumbnailsAsync();
        }
    }

    /// <summary>Called when this item's row is realized; decodes the thumbnails once, and again
    /// after <see cref="ReloadThumbnails"/> if the row is still on screen.</summary>
    public void EnsureThumbnailsDecoded()
    {
        if (_decodeRequested)
        {
            return;
        }

        _decodeRequested = true;
        _ = DecodeWhenReadyAsync();
    }

    private async Task DecodeWhenReadyAsync()
    {
        await ThumbnailsReadyTask;
        await DecodeThumbnailsAsync();
    }

    private async Task DecodeThumbnailsAsync()
    {
        var paths = _thumbnailPaths;
        if (paths.Count == 0)
        {
            return;
        }

        // Decoding is real CPU/disk work; doing it on a background thread keeps the
        // UI thread free while the results grid virtualizes rows in and out of view.
        var bitmaps = await Task.Run(() => paths.Select(DecodeThumbnail).Where(b => b is not null).Cast<BitmapImage>().ToList());
        Thumbnails.Clear();
        foreach (var bitmap in bitmaps)
        {
            Thumbnails.Add(bitmap);
        }
    }

    private static BitmapImage? DecodeThumbnail(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.DecodePixelWidth = 256;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public string FileName => Model.FileName;
    public long SizeBytes => Model.SizeBytes;
    public string SizeDisplay => FormatBytes(Model.SizeBytes);
    public string DurationDisplay => Model.Duration is { } d ? d.ToString(@"m\:ss") : "—";
    public string ResolutionDisplay => Model.Width > 0 ? $"{Model.Width}×{Model.Height}" : "—";
    public string BitRateDisplay => Model.BitRateBps is { } b ? $"{b / 1_000_000.0:0.0} Mbps" : "—";
    public string AudioDisplay => Model.AudioSampleRateHz is { } a ? $"{a / 1000} kHz" : "—";
    // One decimal place, not zero — rounding to a whole number was hiding the difference
    // between a genuine 100.0% (byte-identical) match and, say, 99.6%, which made two
    // merely very-similar (not identical) items look indistinguishable from an exact copy.
    public string MatchDisplay => $"{Model.SimilarityToReferencePercent:0.0}%";
    public string HashHex => Model.HashHex;
    public bool IsVideo => Model.Kind == MediaKind.Video;

    // The single canonical generated thumbnail (JPEG frame for a video, resized copy for
    // a photo) — stable regardless of the user's display thumbnail count, used as the
    // sole input for the "sort sets by thumbnail similarity" feature, per that feature's
    // requirement.
    public string ThumbnailPath => ThumbnailService.PathFor(Model);

    [RelayCommand]
    private void Play()
    {
        if (IsVideo)
        {
            PlayRequested?.Invoke(this);
        }
    }

    [RelayCommand]
    private void OpenExternally()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Model.FullPath) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Best-effort: if no associated app exists, there's nothing more we can do here.
        }
    }

    public static string FormatBytes(long bytes)
    {
        double mb = bytes / 1024.0 / 1024.0;
        return mb >= 1024 ? $"{mb / 1024.0:0.00} GB" : $"{mb:0.0} MB";
    }
}
