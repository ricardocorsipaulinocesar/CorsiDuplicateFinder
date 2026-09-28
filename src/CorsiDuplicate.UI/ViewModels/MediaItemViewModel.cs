using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CorsiDuplicate.Core.Models;
using CorsiDuplicate.Infrastructure.Media;

namespace CorsiDuplicate.UI.ViewModels;

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

    public ObservableCollection<BitmapImage> Thumbnails { get; } = new();

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke(this);

    /// <summary>Re-fetches/regenerates this item's thumbnails — called after the user
    /// changes <see cref="ThumbnailsPerVideo"/> so already-displayed rows update too.</summary>
    public void ReloadThumbnails() => ThumbnailsReadyTask = LoadThumbnailsAsync();

    private async Task LoadThumbnailsAsync()
    {
        var count = IsVideo ? ThumbnailsPerVideo : 1;
        var paths = await ThumbnailService.EnsureThumbnailsAsync(Model, count);
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
