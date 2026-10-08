using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CorsiDuplicate.Core.Abstractions;
using CorsiDuplicate.Core.Grouping;
using CorsiDuplicate.Core.Matching;
using CorsiDuplicate.Core.Models;
using CorsiDuplicate.Infrastructure.IO;
using CorsiDuplicate.Infrastructure.Logging;
using CorsiDuplicate.Infrastructure.Matching;
using CorsiDuplicate.Infrastructure.Media;
using CorsiDuplicate.Infrastructure.Persistence;
using CorsiDuplicate.Infrastructure.Scanning;
using Microsoft.Win32;

namespace CorsiDuplicate.UI.ViewModels;

/// <summary>
/// Carries both pieces the folder-wide filter row's toggle command needs from a single
/// CommandParameter binding: which folder, and which field to filter/sort the whole
/// folder by ("Thumb", "File", "Size", "Length", "Resolution", "Bit rate", "Audio",
/// "Match", or "Hash" — matching each filter button's own Tag/Content).
/// </summary>
public sealed record FolderFilterRequest(FolderResultsViewModel Folder, string FilterKey);

public partial class MainViewModel : ObservableObject
{
    private readonly ScanPipeline _scanPipeline = new();
    private readonly OrbStructuralMatcher _structuralMatcher = new();
    private readonly IGroupingStrategy _grouper;
    private readonly IRecycleBinService _recycleBin = new RecycleBinService();

    // Dedicated scorer for the "sort sets by thumbnail similarity" feature — separate
    // from _structuralMatcher (used for full-resolution duplicate detection) since this
    // one only ever sees the small generated thumbnail images.
    private static readonly ThumbnailSimilarityScorer ThumbnailScorer = new();

    // Raw scanned items per folder, kept in memory so the similarity slider can
    // re-group instantly without ever touching disk/FFmpeg again.
    private readonly Dictionary<string, List<MediaItem>> _scannedItemsByFolder = new();
    private readonly DispatcherTimer _regroupDebounceTimer;

    // Preserves each folder node's expand/collapse state across a re-group, and
    // decides the default the first time a folder appears (first folder expanded,
    // subsequent ones collapsed) without resetting a state the user already toggled.
    private readonly Dictionary<string, bool> _folderExpandState = new();

    // Grouping (especially ORB structural matching) is CPU-heavy; this serializes
    // background grouping runs and lets a newer request (slider moved again, next
    // folder finished scanning) discard a stale one instead of applying it.
    private readonly SemaphoreSlim _regroupLock = new(1, 1);
    private int _regroupRequestVersion;

    private CancellationTokenSource? _scanCts;

    private DateTime _scanStartUtc;
    private DateTime _currentFolderStartUtc;

    public FolderManagerViewModel FolderManager { get; } = new();
    public SelectionSummaryViewModel Selection { get; } = new();
    public ObservableCollection<FolderResultsViewModel> FolderResults { get; } = new();

    [ObservableProperty]
    private double _similarityThreshold = 90;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopScanCommand))]
    private bool _isScanning;

    [ObservableProperty]
    private string _statusMessage = "Ready.";

    // Drives a small "thinking" overlay for any slow filter/results action (currently
    // only the thumbnail-similarity sort, since ORB structural matching can take a
    // moment) — a future slow action can reuse it by setting ProcessingMessage and
    // toggling IsProcessing the same way. Cleared automatically once the action finishes.
    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private string _processingMessage = "Processing...";

    [ObservableProperty]
    private double _thumbnailColumnWidth = 120;

    [ObservableProperty]
    private int _scanProgressCurrent;

    [ObservableProperty]
    private int _scanProgressTotal;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    // Lightweight, non-blocking indicator (unlike IsProcessing's modal) that a
    // similarity-slider-triggered regroup is running in the background — the
    // async/debounced regroup previously gave no visible sign anything was happening.
    [ObservableProperty]
    private bool _isRegrouping;

    [ObservableProperty]
    private bool _isThumbnailsMenuOpen;

    // Thumbnail images are generated in the background, independently of the scan/grouping
    // step that produces the result rows — with several thumbnails per video this can still
    // be running well after "Scan complete" would otherwise show. Tracks every newly created
    // item's own thumbnail task so the final status message waits for them too, instead of
    // reporting done while images are still visibly popping in one by one.
    [ObservableProperty]
    private bool _isGeneratingThumbnails;

    private readonly List<Task> _pendingThumbnailTasks = new();

    // "Contained" folder filter: whole-video fingerprints (cached on disk) and the way to
    // cancel the analysis from the processing modal, the only cancellable action there.
    private readonly VideoFingerprintCache _fingerprintCache = new();
    private CancellationTokenSource? _processingCts;

    // Last Contained analysis per folder, and the folders currently showing it — regrouping
    // (slider, next folder finishing) rebuilds every folder's Sets, so those folders get their
    // Contained view re-applied instead of silently falling back to duplicate Sets.
    private readonly Dictionary<string, ContainmentResult> _containmentByFolder = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _containedViewFolders = new(StringComparer.OrdinalIgnoreCase);

    // "Videos only" scan mode: photos are skipped and every folder gets the Contained analysis
    // right after its duplicate grouping. Persisted like ThumbnailsPerVideo.
    [ObservableProperty]
    private bool _scanVideosOnly;

    partial void OnScanVideosOnlyChanged(bool value)
    {
        var settings = _appSettingsStore.Load();
        settings.ScanVideosOnly = value;
        _appSettingsStore.Save(settings);
    }

    [RelayCommand]
    private void SetScanMode(string? mode) => ScanVideosOnly = mode == "Videos";

    [ObservableProperty]
    private bool _isProcessingCancellable;

    private readonly AppSettingsStore _appSettingsStore = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DecrementThumbnailsPerVideoCommand))]
    private int _thumbnailsPerVideo = 1;

    public bool HasAnyResults => FolderResults.Count > 0;

    public string AppVersion { get; } = $"v{typeof(MainViewModel).Assembly.GetName().Version}";

    public MainViewModel()
    {
        _grouper = new HammingClusterGrouper(new HsvHistogramComparer(), _structuralMatcher);
        _regroupDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _regroupDebounceTimer.Tick += async (_, _) =>
        {
            _regroupDebounceTimer.Stop();
            await RebuildResultsFromMemoryAsync();
        };

        FolderManager.FolderRemoved += OnFolderRemoved;

        var settings = _appSettingsStore.Load();
        ThumbnailsPerVideo = Math.Max(1, settings.ThumbnailsPerVideo);
        _scanVideosOnly = settings.ScanVideosOnly;
        MediaItemViewModel.ThumbnailsPerVideo = ThumbnailsPerVideo;
    }

    [RelayCommand]
    private void ToggleThumbnailsMenu() => IsThumbnailsMenuOpen = !IsThumbnailsMenuOpen;

    [RelayCommand(CanExecute = nameof(CanDecrementThumbnailsPerVideo))]
    private void DecrementThumbnailsPerVideo() => SetThumbnailsPerVideo(ThumbnailsPerVideo - 1);

    private bool CanDecrementThumbnailsPerVideo() => ThumbnailsPerVideo > 1;

    [RelayCommand]
    private void IncrementThumbnailsPerVideo() => SetThumbnailsPerVideo(ThumbnailsPerVideo + 1);

    /// <summary>
    /// 1–7 thumbnails per video, persisted immediately, and applied to every video row
    /// already on screen (not just future scans) so the change is visible right away.
    /// </summary>
    private void SetThumbnailsPerVideo(int value)
    {
        value = Math.Clamp(value, 1, 7);
        if (value == ThumbnailsPerVideo)
        {
            return;
        }

        ThumbnailsPerVideo = value;
        MediaItemViewModel.ThumbnailsPerVideo = value;
        var settings = _appSettingsStore.Load();
        settings.ThumbnailsPerVideo = value;
        _appSettingsStore.Save(settings);

        foreach (var item in FolderResults.SelectMany(f => f.Groups).SelectMany(g => g.Items).Where(i => i.IsVideo))
        {
            item.ReloadThumbnails();
        }
    }

    /// <summary>
    /// A folder removed from the managed list must also drop whatever results/in-memory
    /// scan data it still has showing — otherwise a stale duplicate-set for a folder the
    /// user just removed keeps sitting in the results grid until the next full re-scan.
    /// </summary>
    private void OnFolderRemoved(string folderPath)
    {
        _scannedItemsByFolder.Remove(folderPath);
        _folderExpandState.Remove(folderPath);

        var folderVm = FolderResults.FirstOrDefault(f => string.Equals(f.Path, folderPath, StringComparison.OrdinalIgnoreCase));
        if (folderVm is not null)
        {
            FolderResults.Remove(folderVm);
            Selection.Recompute(FolderResults.SelectMany(f => f.Groups).SelectMany(g => g.Items).Distinct());
            OnPropertyChanged(nameof(HasAnyResults));
        }

        AppLogger.Info(nameof(MainViewModel), nameof(OnFolderRemoved), $"Cleared results and cached scan data for removed folder '{folderPath}'.");
    }

    partial void OnSimilarityThresholdChanged(double value)
    {
        if (_scannedItemsByFolder.Count == 0)
        {
            return;
        }

        _regroupDebounceTimer.Stop();
        _regroupDebounceTimer.Start();
    }

    [RelayCommand]
    private void AddFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Select one or more folders to scan", Multiselect = true };
        if (dialog.ShowDialog() == true)
        {
            var added = 0;
            var alreadyPresent = 0;
            foreach (var folderName in dialog.FolderNames)
            {
                if (FolderManager.AddFolder(folderName))
                {
                    added++;
                }
                else
                {
                    alreadyPresent++;
                }
            }

            StatusMessage = (added, alreadyPresent) switch
            {
                (0, > 0) => "Those folders are already in the list.",
                (> 0, 0) => $"Added {added} folder(s).",
                (> 0, > 0) => $"Added {added} folder(s); {alreadyPresent} were already in the list.",
                _ => StatusMessage
            };
        }
    }

    [RelayCommand]
    private void ApplyHighQualityToAll()
    {
        foreach (var group in FolderResults.SelectMany(f => f.Groups))
        {
            group.SelectHighQualityCommand.Execute(null);
        }
    }

    [RelayCommand]
    private void ApplyLowQualityToAll()
    {
        foreach (var group in FolderResults.SelectMany(f => f.Groups))
        {
            group.SelectLowQualityCommand.Execute(null);
        }
    }

    /// <summary>
    /// Clears every duplicate set currently on screen without re-scanning — also drops
    /// the in-memory scanned items and expand-state, so a later slider move or re-scan
    /// starts clean instead of silently repopulating the grid from stale data.
    /// </summary>
    [RelayCommand]
    private void ClearResults()
    {
        AppLogger.Info(nameof(MainViewModel), nameof(ClearResults), $"Clearing {FolderResults.Count} folder result(s) from the grid.");

        FolderResults.Clear();
        _scannedItemsByFolder.Clear();
        _folderExpandState.Clear();
        Selection.Reset();
        OnPropertyChanged(nameof(HasAnyResults));
        StatusMessage = "Results cleared.";
    }

    /// <summary>Collapses every folder currently expanded on screen, without discarding any results.</summary>
    [RelayCommand]
    private void CollapseAllFolders()
    {
        foreach (var folder in FolderResults)
        {
            folder.IsExpanded = false;
        }
    }

    /// <summary>
    /// Toggles a folder between its normal detection-order Sets and a single flattened
    /// view that ignores Set boundaries entirely: every row (item) across every Set in
    /// that folder — and only that folder — is reordered by one field ("Thumb" via
    /// generated-thumbnail similarity, everything else via a plain comparison), as one
    /// new result. Toggling the same filter again restores the exact original Sets;
    /// clicking a different filter while one is active switches straight to it. Every
    /// filter action (not just "Thumb") runs off the UI thread and shows the
    /// <see cref="IsProcessing"/> overlay for its duration, so there's always a visible
    /// "something is happening" signal even for the fast, simple-comparison filters.
    /// </summary>
    [RelayCommand]
    private async Task ToggleFolderFilterAsync(FolderFilterRequest? request)
    {
        if (request is not { Folder: { } folder, FilterKey: { } key })
        {
            return;
        }

        if (folder.ActiveFolderFilter == key)
        {
            RestoreFolderView(folder);
            return;
        }

        if (folder.ActiveFolderFilter == ContainedFilterKey)
        {
            // Other filters re-sort the Sets' items; leave the clip-only view first.
            RestoreFolderView(folder);
        }

        if (key == ContainedFilterKey)
        {
            await ShowContainedVideosAsync(folder);
            return;
        }

        var allItems = folder.Groups.SelectMany(g => g.Items).ToList();
        if (allItems.Count < 2)
        {
            return;
        }

        ProcessingMessage = key == "Thumb" ? "Comparing thumbnails..." : $"Sorting by {key.ToLowerInvariant()}...";
        IsProcessing = true;
        List<MediaItemViewModel> orderedItems;
        try
        {
            var workTask = key == "Thumb"
                ? Task.Run(() => OrderItemsByThumbnailSimilarity(allItems))
                : Task.Run(() => OrderItemsByField(allItems, key));

            // The plain-field sorts finish in well under a millisecond — fast enough
            // that IsProcessing could flip true then false again before WPF ever paints
            // a frame with the overlay visible, making the modal appear not to show at
            // all. Waiting on this alongside the real work guarantees the overlay is up
            // long enough to actually be seen, for every filter, not just "Thumb".
            await Task.WhenAll(workTask, Task.Delay(300));
            orderedItems = await workTask;
        }
        catch (Exception ex)
        {
            AppLogger.Error(nameof(MainViewModel), nameof(ToggleFolderFilterAsync),
                $"Failed to filter folder '{folder.Path}' by '{key}': {ex.Message}");
            return;
        }
        finally
        {
            IsProcessing = false;
        }

        var flatGroup = new DuplicateGroupViewModel(new DuplicateGroup(), folder.Path, orderedItems)
        {
            SetNumber = 1,
            CustomLabel = $"All results — sorted by {key.ToLowerInvariant()}",
            Owner = folder
        };
        folder.ApplyFlattenedOrder(new List<DuplicateGroupViewModel> { flatGroup }, key);
    }

    public const string ContainedFilterKey = "Contained";

    private void RestoreFolderView(FolderResultsViewModel folder)
    {
        _containedViewFolders.Remove(folder.Path);
        foreach (var item in folder.Groups.SelectMany(g => g.Items))
        {
            item.ClearContainment();
        }
        folder.RestoreOriginalGroupOrder();
        Selection.Recompute(FolderResults.SelectMany(f => f.Groups).SelectMany(g => g.Items).Distinct());
    }

    [RelayCommand]
    private void CancelProcessing() => _processingCts?.Cancel();

    /// <summary>Folder "Contained" button: same analysis as a videos-only scan, but behind
    /// the cancellable processing modal.</summary>
    private async Task ShowContainedVideosAsync(FolderResultsViewModel folder)
    {
        _processingCts = new CancellationTokenSource();
        IsProcessingCancellable = true;
        IsProcessing = true;
        ContainmentResult? result;
        try
        {
            var progress = new Progress<string>(message => ProcessingMessage = message);
            result = await ComputeContainmentAsync(folder.Path, progress, _processingCts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Contained analysis cancelled.";
            return;
        }
        finally
        {
            IsProcessing = false;
            IsProcessingCancellable = false;
            _processingCts.Dispose();
            _processingCts = null;
        }

        if (result is null)
        {
            StatusMessage = "Contained: this folder needs at least two videos.";
            return;
        }

        if (result.IsEmpty)
        {
            StatusMessage = $"No contained videos found in {FolderDisplayName(folder.Path)}.";
            return;
        }

        _containmentByFolder[folder.Path] = result;
        _containedViewFolders.Add(folder.Path);
        ApplyContainedView(folder, result);
        StatusMessage = $"Contained: {result.Summary}.";
    }

    /// <summary>
    /// Fingerprints every video scanned in the folder (several at once; each is decoded only
    /// the first time, then read from the on-disk cache), then compares every pair. Null when
    /// the folder has fewer than two videos.
    /// </summary>
    private async Task<ContainmentResult?> ComputeContainmentAsync(string folderPath, IProgress<string> progress, CancellationToken ct)
    {
        if (!_scannedItemsByFolder.TryGetValue(folderPath, out var scanned))
        {
            return null;
        }

        var videos = scanned.Where(i => i.Kind == MediaKind.Video).ToList();
        if (videos.Count < 2)
        {
            return null;
        }

        var folderName = FolderDisplayName(folderPath);
        var fingerprints = new System.Collections.Concurrent.ConcurrentDictionary<MediaItem, VideoFingerprint>();
        var done = 0;
        progress.Report($"Analyzing videos 0/{videos.Count} - {folderName}");

        await Parallel.ForEachAsync(videos,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4), CancellationToken = ct },
            async (video, token) =>
            {
                try
                {
                    if (await _fingerprintCache.GetOrCreateAsync(video, token) is { } fingerprint)
                    {
                        fingerprints[video] = fingerprint;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AppLogger.Error(nameof(MainViewModel), nameof(ComputeContainmentAsync),
                        $"Could not fingerprint '{video.FullPath}'.", ex);
                }

                progress.Report($"Analyzing videos {Interlocked.Increment(ref done)}/{videos.Count} - {folderName}");
            });

        progress.Report($"Comparing videos - {folderName}");
        return await Task.Run(() => ClassifyContainment(fingerprints, ct), ct);
    }

    private sealed record ContainedClip(MediaItem Clip, MediaItem Source, int SourceSeconds, ContainmentMatch Match);

    private sealed record CompilationSource(MediaItem Source, int SourceSeconds, IReadOnlyList<SharedSegment> Segments);

    private sealed record Compilation(MediaItem Video, int Seconds, IReadOnlyList<CompilationSource> Sources);

    private sealed record ContainmentResult(IReadOnlyList<ContainedClip> Clips, IReadOnlyList<Compilation> Compilations)
    {
        public bool IsEmpty => Clips.Count == 0 && Compilations.Count == 0;

        public string Summary
        {
            get
            {
                var parts = new List<string>();
                if (Clips.Count > 0)
                {
                    parts.Add($"{Clips.Count} clip(s) found inside {Clips.Select(c => c.Source).Distinct().Count()} video(s)");
                }
                if (Compilations.Count > 0)
                {
                    parts.Add($"{Compilations.Count} compilation(s)");
                }
                return string.Join(", ", parts);
            }
        }
    }

    /// <summary>
    /// Compares every ordered pair. A video found inside a longer one is shown under its best
    /// source. A video that isn't a clip but shares stretches with two or more videos (or with
    /// one, covering at least 30 % of it) is a compilation — its own clips and duplicate copies
    /// of it are not counted as its sources.
    /// </summary>
    private static ContainmentResult ClassifyContainment(
        IReadOnlyDictionary<MediaItem, VideoFingerprint> fingerprints, CancellationToken ct)
    {
        var videos = fingerprints.Keys.ToList();
        var comparisons = new ContainmentComparison?[videos.Count, videos.Count];

        Parallel.For(0, videos.Count, new ParallelOptions { CancellationToken = ct }, a =>
        {
            for (var b = 0; b < videos.Count; b++)
            {
                if (a != b)
                {
                    ct.ThrowIfCancellationRequested();
                    comparisons[a, b] = ContainmentMatcher.Compare(fingerprints[videos[a]], fingerprints[videos[b]]);
                }
            }
        });

        var containedIn = new int?[videos.Count];
        var clips = new List<ContainedClip>();
        for (var a = 0; a < videos.Count; a++)
        {
            ContainmentMatch? best = null;
            for (var b = 0; b < videos.Count; b++)
            {
                if (comparisons[a, b]?.Contained is { } match && (best is null || match.CoveragePercent > best.CoveragePercent))
                {
                    best = match;
                    containedIn[a] = b;
                }
            }

            if (best is not null && containedIn[a] is { } source)
            {
                clips.Add(new ContainedClip(videos[a], videos[source], fingerprints[videos[source]].Length, best));
            }
        }

        var compilations = new List<Compilation>();
        for (var a = 0; a < videos.Count; a++)
        {
            if (containedIn[a] is not null)
            {
                continue;
            }

            var length = fingerprints[videos[a]].Length;
            var sources = new List<CompilationSource>();
            for (var b = 0; b < videos.Count; b++)
            {
                if (a == b || containedIn[b] == a || comparisons[a, b] is not { Segments.Count: > 0 } comparison)
                {
                    continue;
                }

                var shared = comparison.Segments.Sum(s => s.Seconds);
                var otherLength = fingerprints[videos[b]].Length;
                var isDuplicateCopy = shared >= length * 0.8
                    && Math.Abs(length - otherLength) <= Math.Max(length, otherLength) * (1 - ContainmentMatcher.MaxClipToSourceLengthRatio);
                if (!isDuplicateCopy)
                {
                    sources.Add(new CompilationSource(videos[b], otherLength, comparison.Segments));
                }
            }

            var covered = sources.Sum(s => s.Segments.Sum(seg => seg.Seconds));
            if (sources.Count >= 2 || (sources.Count == 1 && covered >= length * 0.3))
            {
                compilations.Add(new Compilation(videos[a], length, sources));
            }
        }

        return new ContainmentResult(clips, compilations);
    }

    /// <summary>Replaces the folder's Sets with one Set per source video (its clips below it)
    /// and one per compilation (its source videos below it). Click "Contained" again to go back.</summary>
    private void ApplyContainedView(FolderResultsViewModel folder, ContainmentResult result,
        IReadOnlyDictionary<string, MediaItemViewModel>? knownItems = null)
    {
        foreach (var item in folder.Groups.SelectMany(g => g.Items))
        {
            item.ClearContainment();
        }

        // Reuse every item view model that already exists (keeps thumbnails and check state).
        var existing = folder.Groups.SelectMany(g => g.Items)
            .Concat(knownItems?.Values ?? Enumerable.Empty<MediaItemViewModel>())
            .GroupBy(i => i.Model.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        MediaItemViewModel VmFor(MediaItem item) =>
            existing.TryGetValue(item.FullPath, out var vm) ? vm : existing[item.FullPath] = CreateItemViewModel(item, folder.Path);

        var groups = new List<DuplicateGroupViewModel>();
        var setNumber = 1;
        foreach (var bySource in result.Clips.GroupBy(c => c.Source).OrderByDescending(g => g.Count()))
        {
            var clipCount = bySource.Count();
            var sourceSeconds = bySource.First().SourceSeconds;
            var mainName = bySource.Key.FileName;
            var videosWord = clipCount == 1 ? "video" : "videos";
            var sourceVm = VmFor(bySource.Key);
            sourceVm.SetContainment("Main video", MediaItemViewModel.MainRoleColor, isChild: false,
                clipCount == 1 ? "The video below is an excerpt of this video." : $"The {clipCount} videos below are excerpts of this video.",
                MediaItemViewModel.ClipColor,
                bySource.Select(c => (c.Match.StartSeconds, c.Match.EndSeconds)), sourceSeconds);

            var rows = new List<MediaItemViewModel> { sourceVm };
            foreach (var clip in bySource.OrderBy(c => c.Match.StartSeconds))
            {
                var clipVm = VmFor(clip.Clip);
                clipVm.SetContainment("Excerpt of the main video", MediaItemViewModel.ExcerptRoleColor, isChild: true,
                    $"This video is inside {mainName}, from {FormatSeconds(clip.Match.StartSeconds)} to " +
                    $"{FormatSeconds(clip.Match.EndSeconds)}{(clip.Match.IsMirrored ? " (mirrored)" : "")}.",
                    MediaItemViewModel.ClipColor,
                    new[] { (clip.Match.StartSeconds, clip.Match.EndSeconds) },
                    clip.SourceSeconds);
                rows.Add(clipVm);
            }

            groups.Add(new DuplicateGroupViewModel(new DuplicateGroup(), folder.Path, rows)
            {
                SetNumber = setNumber++,
                CustomLabel = $"{mainName} has {clipCount} {videosWord} inside it",
                Owner = folder,
            });
        }

        foreach (var compilation in result.Compilations)
        {
            var allSegments = compilation.Sources.SelectMany(s => s.Segments).ToList();
            var compilationVm = VmFor(compilation.Video);
            var compName = compilation.Video.FileName;
            var sourceCount = compilation.Sources.Count;
            compilationVm.SetContainment("Compilation", MediaItemViewModel.CompilationColor, isChild: false,
                sourceCount == 1 ? "This video uses parts of the video below." : $"This video uses parts of the {sourceCount} videos below.",
                MediaItemViewModel.CompilationColor,
                allSegments.Select(s => (s.ClipStart, s.ClipEnd)),
                compilation.Seconds);

            var rows = new List<MediaItemViewModel> { compilationVm };
            foreach (var source in compilation.Sources)
            {
                var sourceVm = VmFor(source.Source);
                var notes = source.Segments.Select(s =>
                    $"{FormatSeconds(s.ClipStart)}–{FormatSeconds(s.ClipEnd)} of {compName} comes from this video " +
                    $"({FormatSeconds(s.SourceStart)}–{FormatSeconds(s.SourceEnd)}){(s.IsMirrored ? " (mirrored)" : "")}.");
                sourceVm.SetContainment("Used in the compilation", MediaItemViewModel.ExcerptRoleColor, isChild: true,
                    string.Join("\n", notes), MediaItemViewModel.CompilationColor,
                    source.Segments.Select(s => (s.SourceStart, s.SourceEnd)), source.SourceSeconds);
                rows.Add(sourceVm);
            }

            groups.Add(new DuplicateGroupViewModel(new DuplicateGroup(), folder.Path, rows)
            {
                SetNumber = setNumber++,
                CustomLabel = $"{compName} was made from parts of {sourceCount} video{(sourceCount == 1 ? "" : "s")}",
                Owner = folder,
            });
        }

        folder.ApplyFlattenedOrder(groups, ContainedFilterKey);
        Selection.Recompute(FolderResults.SelectMany(f => f.Groups).SelectMany(g => g.Items).Distinct());
    }

    private FolderResultsViewModel CreateFolderResults(string folderPath, int itemCount)
    {
        if (!_folderExpandState.TryGetValue(folderPath, out var isExpanded))
        {
            // First folder to ever produce results starts expanded; every one after starts collapsed.
            isExpanded = _folderExpandState.Count == 0;
            _folderExpandState[folderPath] = isExpanded;
        }

        var folderVm = new FolderResultsViewModel(folderPath, itemCount) { IsExpanded = isExpanded };
        folderVm.ExpandedChanged += expanded => _folderExpandState[folderPath] = expanded;
        return folderVm;
    }

    /// <summary>The folder's results block, created when the folder had no duplicate Sets
    /// (a videos-only scan can still find clips there).</summary>
    private FolderResultsViewModel EnsureFolderResults(string folderPath, int itemCount)
    {
        var folderVm = FolderResults.FirstOrDefault(f => string.Equals(f.Path, folderPath, StringComparison.OrdinalIgnoreCase));
        if (folderVm is null)
        {
            folderVm = CreateFolderResults(folderPath, itemCount);
            FolderResults.Add(folderVm);
            OnPropertyChanged(nameof(HasAnyResults));
        }
        return folderVm;
    }

    private static string FormatSeconds(int seconds) =>
        seconds >= 3600 ? TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss") : TimeSpan.FromSeconds(seconds).ToString(@"m\:ss");

    /// <summary>Plain, synchronous comparison for every folder-wide filter except "Thumb".</summary>
    private static List<MediaItemViewModel> OrderItemsByField(List<MediaItemViewModel> items, string key) => key switch
    {
        "File" => items.OrderBy(i => i.FileName, StringComparer.OrdinalIgnoreCase).ToList(),
        "Size" => items.OrderByDescending(i => i.Model.SizeBytes).ToList(),
        "Length" => items.OrderByDescending(i => i.Model.Duration ?? TimeSpan.Zero).ToList(),
        "Resolution" => items.OrderByDescending(i => (long)i.Model.Width * i.Model.Height).ToList(),
        "Bit rate" => items.OrderByDescending(i => i.Model.BitRateBps ?? 0).ToList(),
        "Audio" => items.OrderByDescending(i => i.Model.AudioSampleRateHz ?? 0).ToList(),
        "Match" => items.OrderByDescending(i => i.Model.SimilarityToReferencePercent).ToList(),
        "Hash" => items.OrderBy(i => i.HashHex, StringComparer.OrdinalIgnoreCase).ToList(),
        _ => items,
    };

    /// <summary>
    /// Greedy nearest-neighbor chain over every individual item of a folder (never just
    /// one representative per Set): starting from the first item, repeatedly appends
    /// whichever remaining item's thumbnail is most similar to the current one — clusters
    /// visually similar items adjacently regardless of which original Set they came from.
    /// </summary>
    private static List<MediaItemViewModel> OrderItemsByThumbnailSimilarity(List<MediaItemViewModel> items)
    {
        var withThumbnail = items.Where(i => i.ThumbnailPath is not null).ToList();
        var withoutThumbnail = items.Where(i => i.ThumbnailPath is null).ToList();

        var ordered = new List<MediaItemViewModel>();
        if (withThumbnail.Count == 0)
        {
            return items;
        }

        var remaining = new List<MediaItemViewModel>(withThumbnail);
        var current = remaining[0];
        remaining.RemoveAt(0);
        ordered.Add(current);

        while (remaining.Count > 0)
        {
            var bestIndex = 0;
            var bestScore = -1.0;
            for (var i = 0; i < remaining.Count; i++)
            {
                var score = ThumbnailScorer.Compare(current.ThumbnailPath!, remaining[i].ThumbnailPath!);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            current = remaining[bestIndex];
            remaining.RemoveAt(bestIndex);
            ordered.Add(current);
        }

        ordered.AddRange(withoutThumbnail);
        return ordered;
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        AppLogger.Info(nameof(MainViewModel), nameof(ScanAsync), $"Scan requested at similarity threshold {SimilarityThreshold:0}%.");

        var targets = FolderManager.EnabledFolders.ToList();
        if (targets.Count == 0)
        {
            StatusMessage = "Add and enable at least one folder first.";
            return;
        }

        IsScanning = true;
        _scanCts = new CancellationTokenSource();
        _scannedItemsByFolder.Clear();
        Selection.Reset();
        ScanProgressCurrent = 0;
        ScanProgressTotal = 0;
        IsProgressIndeterminate = true;
        _scanStartUtc = DateTime.UtcNow;
        _containmentByFolder.Clear();
        _containedViewFolders.Clear();
        var videosOnly = ScanVideosOnly;

        var cancelled = false;

        try
        {
            foreach (var folder in targets)
            {
                if (_scanCts.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                var folderDisplayName = FolderDisplayName(folder.Path);
                StatusMessage = $"Enumerating files... - {folderDisplayName}";
                IsProgressIndeterminate = true;
                _currentFolderStartUtc = DateTime.UtcNow;
                folder.IsScanning = true;

                try
                {
                    var progress = new Progress<ScanProgress>(p =>
                    {
                        IsProgressIndeterminate = false;
                        ScanProgressCurrent = p.FilesDone;
                        ScanProgressTotal = p.FilesTotal;
                        StatusMessage = $"Scanning... {p.FilesDone}/{p.FilesTotal} - {folderDisplayName}{FormatEta(p)}";
                    });

                    var items = await _scanPipeline.ScanFolderAsync(folder.Path, progress, _scanCts.Token, videosOnly);
                    FolderManager.RecordScanResult(folder.Path, items.Count);
                    _scannedItemsByFolder[folder.Path] = items;

                    // Results appear per folder as each one finishes, instead of waiting
                    // for every enabled folder to complete before showing anything.
                    await RebuildResultsFromMemoryAsync();

                    if (videosOnly)
                    {
                        IsProgressIndeterminate = true;
                        var analysisProgress = new Progress<string>(message => StatusMessage = message);
                        var containment = await ComputeContainmentAsync(folder.Path, analysisProgress, _scanCts.Token);
                        if (containment is { IsEmpty: false })
                        {
                            _containmentByFolder[folder.Path] = containment;
                            _containedViewFolders.Add(folder.Path);
                            ApplyContainedView(EnsureFolderResults(folder.Path, items.Count), containment);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // The folder currently being scanned is abandoned as-is; whatever
                    // earlier folders already finished stays visible in the results.
                    cancelled = true;
                }
                catch (Exception ex)
                {
                    // A bug in one folder's extraction/grouping must not take the whole
                    // scan (or, if left uncaught, the whole app) down with it — log it,
                    // tell the user, and move on to the next folder instead.
                    AppLogger.Error(nameof(MainViewModel), nameof(ScanAsync),
                        $"Scanning folder '{folder.Path}' failed unexpectedly.", ex);
                    StatusMessage = $"Error - {folderDisplayName} - {ex.Message}";
                }
                finally
                {
                    folder.IsScanning = false;
                }

                if (cancelled)
                {
                    break;
                }
            }

            // Grouping is done at this point, but thumbnails for the items it produced may
            // still be generating in the background — wait for those too before telling the
            // user the scan is finished, so "complete" only shows once everything actually is.
            await WaitForPendingThumbnailsAsync();

            if (cancelled)
            {
                StatusMessage = "Scan stopped.";
                AppLogger.Info(nameof(MainViewModel), nameof(ScanAsync), "Scan stopped by user.");
            }
            else if (videosOnly)
            {
                var all = new ContainmentResult(
                    _containmentByFolder.Values.SelectMany(r => r.Clips).ToList(),
                    _containmentByFolder.Values.SelectMany(r => r.Compilations).ToList());
                StatusMessage = all.IsEmpty
                    ? "Scan complete. No contained videos found."
                    : $"Scan complete. {all.Summary}.";
                AppLogger.Info(nameof(MainViewModel), nameof(ScanAsync), $"Videos-only scan complete: {StatusMessage}");
            }
            else
            {
                var totalGroups = FolderResults.Sum(f => f.Groups.Count);
                StatusMessage = totalGroups == 0
                    ? "Scan complete. No duplicates found."
                    : $"Scan complete. {totalGroups} duplicate set(s) found.";
                AppLogger.Info(nameof(MainViewModel), nameof(ScanAsync), $"Scan complete. {totalGroups} duplicate set(s) found across {targets.Count} folder(s).");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Fatal(nameof(MainViewModel), nameof(ScanAsync), "Scan aborted by an unhandled exception.", ex);
            StatusMessage = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    /// <summary>
    /// The status bar shows only the folder's final path segment, not the full path —
    /// the user already knows which drive/parent folders they added, and the full path
    /// just crowds out the actual progress. Falls back to the full path for a bare
    /// drive root (e.g. "D:\"), which has no final segment of its own.
    /// </summary>
    private static string FolderDisplayName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    [RelayCommand(CanExecute = nameof(IsScanning))]
    private void StopScan()
    {
        if (_scanCts is { IsCancellationRequested: false })
        {
            _scanCts.Cancel();
            StatusMessage = "Stopping scan...";
        }
    }

    /// <summary>
    /// Estimates remaining time from the current folder's own throughput so far
    /// (files/second since this folder started scanning) — simple and self-correcting
    /// as extraction speed varies file-to-file, without needing a separate calibration pass.
    /// </summary>
    private string FormatEta(ScanProgress p)
    {
        if (p.FilesDone < 3 || p.FilesDone >= p.FilesTotal)
        {
            return string.Empty;
        }

        var elapsed = DateTime.UtcNow - _currentFolderStartUtc;
        if (elapsed.TotalSeconds < 1)
        {
            return string.Empty;
        }

        var rate = p.FilesDone / elapsed.TotalSeconds;
        if (rate <= 0)
        {
            return string.Empty;
        }

        var remainingSeconds = (p.FilesTotal - p.FilesDone) / rate;
        var remaining = TimeSpan.FromSeconds(remainingSeconds);

        var display = remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}h {remaining.Minutes}m"
            : remaining.TotalMinutes >= 1
                ? $"{(int)remaining.TotalMinutes}m {remaining.Seconds}s"
                : $"{Math.Max(1, remaining.Seconds)}s";

        return $" - about {display} remaining";
    }

    /// <summary>
    /// Re-runs grouping over the already-scanned, already-hashed in-memory items —
    /// no disk/FFmpeg activity — so moving the similarity slider re-clusters without
    /// touching disk again. The clustering itself (including on-demand ORB structural
    /// matching) is still CPU-heavy, so it runs on a background thread; only building
    /// the observable view-model collections happens back on the UI thread. Existing
    /// MediaItemViewModel instances are reused by path so checkbox selection survives
    /// a re-group.
    /// </summary>
    private async Task RebuildResultsFromMemoryAsync()
    {
        var myVersion = Interlocked.Increment(ref _regroupRequestVersion);

        var existingByPath = FolderResults
            .SelectMany(f => f.Groups)
            .SelectMany(g => g.Items)
            .DistinctBy(i => i.Model.FullPath)
            .ToDictionary(i => i.Model.FullPath);

        var snapshot = _scannedItemsByFolder
            .Select(kv => (kv.Key, Items: kv.Value.ToList()))
            .ToList();
        var threshold = SimilarityThreshold;

        List<(string FolderPath, List<DuplicateGroup> Groups, int ItemCount)> perFolder;
        IsRegrouping = true;
        await _regroupLock.WaitAsync();
        try
        {
            if (myVersion != _regroupRequestVersion)
            {
                // Superseded (slider moved again / next folder finished) before this
                // request even got its turn — let the newer one win instead.
                return;
            }

            perFolder = await Task.Run(() => snapshot
                .Select(f => (f.Key, _grouper.Group(f.Items, threshold), f.Items.Count))
                .ToList());
        }
        catch (Exception ex)
        {
            AppLogger.Error(nameof(MainViewModel), nameof(RebuildResultsFromMemoryAsync),
                $"Grouping failed at similarity threshold {threshold:0}%.", ex);
            StatusMessage = $"Grouping failed: {ex.Message}";
            return;
        }
        finally
        {
            _regroupLock.Release();
            IsRegrouping = false;
        }

        if (myVersion != _regroupRequestVersion)
        {
            // Stale by the time grouping finished — a newer request will apply its own results.
            return;
        }

        // Reused item view models must not carry a previous "Contained" view's notes into
        // the freshly grouped Sets.
        foreach (var item in existingByPath.Values)
        {
            item.ClearContainment();
        }

        FolderResults.Clear();

        foreach (var (folderPath, groups, itemCount) in perFolder)
        {
            var showContained = _containedViewFolders.Contains(folderPath)
                && _containmentByFolder.TryGetValue(folderPath, out _);
            if (groups.Count == 0 && !showContained)
            {
                continue;
            }

            var folderVm = CreateFolderResults(folderPath, itemCount);

            var setNumber = 1;
            foreach (var group in groups)
            {
                var itemVms = group.Items
                    .Select(i => existingByPath.TryGetValue(i.FullPath, out var existing)
                        ? existing
                        : CreateItemViewModel(i, folderPath))
                    .ToList();
                folderVm.Groups.Add(new DuplicateGroupViewModel(group, folderPath, itemVms) { SetNumber = setNumber++, Owner = folderVm });
            }
            FolderResults.Add(folderVm);

            if (showContained)
            {
                ApplyContainedView(folderVm, _containmentByFolder[folderPath], existingByPath);
            }
        }

        Selection.Recompute(FolderResults.SelectMany(f => f.Groups).SelectMany(g => g.Items).Distinct());
        OnPropertyChanged(nameof(HasAnyResults));
    }

    private MediaItemViewModel CreateItemViewModel(MediaItem item, string folderPath)
    {
        var vm = new MediaItemViewModel(item, folderPath);
        vm.SelectionChanged += _ => Selection.OnSelectionChanged(vm);
        vm.PlayRequested += OnPlayRequested;
        _pendingThumbnailTasks.Add(vm.ThumbnailsReadyTask);
        return vm;
    }

    /// <summary>Waits for every thumbnail generated since the last call to finish, updating
    /// <see cref="StatusMessage"/> with a running count so the user can see there's still
    /// work happening even after grouping itself is done. A no-op when nothing is pending.</summary>
    private async Task WaitForPendingThumbnailsAsync()
    {
        var pending = _pendingThumbnailTasks.Where(t => !t.IsCompleted).ToList();
        _pendingThumbnailTasks.Clear();
        if (pending.Count == 0)
        {
            return;
        }

        IsGeneratingThumbnails = true;
        var total = pending.Count;
        var done = 0;
        StatusMessage = $"Generating thumbnails... 0/{total}";

        await Task.WhenAll(pending.Select(async task =>
        {
            try
            {
                await task;
            }
            catch (Exception ex)
            {
                AppLogger.Error(nameof(MainViewModel), nameof(WaitForPendingThumbnailsAsync),
                    "A thumbnail failed to generate.", ex);
            }

            var finished = Interlocked.Increment(ref done);
            StatusMessage = $"Generating thumbnails... {finished}/{total}";
        }));

        IsGeneratingThumbnails = false;
    }

    private void OnPlayRequested(MediaItemViewModel item)
    {
        var window = new Views.VideoPlayerWindow(item.Model.FullPath, item.FileName)
        {
            Owner = Application.Current.MainWindow
        };
        window.Show();
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        // Distinct: in the "Contained" view one video can be both a clip and a source row.
        var allItems = FolderResults.SelectMany(f => f.Groups).SelectMany(g => g.Items).Distinct().ToList();
        var selected = allItems.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        AppLogger.Info(nameof(MainViewModel), nameof(DeleteSelectedAsync), $"Deleting {selected.Count} selected file(s).");

        var confirm = MessageBox.Show(
            $"Move {selected.Count} file(s) ({MediaItemViewModel.FormatBytes(selected.Sum(i => i.SizeBytes))}) to the Recycle Bin?",
            "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        var failures = new List<string>();
        var failedItems = new HashSet<MediaItemViewModel>();
        await Task.Run(() =>
        {
            foreach (var item in selected)
            {
                if (!_recycleBin.TrySendToRecycleBin(item.Model.FullPath, out var error))
                {
                    failures.Add($"{item.FileName}: {error}");
                    failedItems.Add(item);
                    AppLogger.Warning(nameof(MainViewModel), nameof(DeleteSelectedAsync),
                        $"Failed to send '{item.Model.FullPath}' to the Recycle Bin: {error}");
                }
            }
        });

        var deleted = selected.Where(i => !failedItems.Contains(i)).ToList();
        var deletedPaths = deleted.Select(i => i.Model.FullPath).ToHashSet();

        foreach (var list in _scannedItemsByFolder.Values)
        {
            list.RemoveAll(i => deletedPaths.Contains(i.FullPath));
        }

        foreach (var item in deleted)
        {
            // These items are about to leave every collection they're in; clearing the
            // flag first means nothing can mistake them for still being "selected".
            item.IsSelected = false;
        }

        foreach (var folderVm in FolderResults.ToList())
        {
            foreach (var group in folderVm.Groups.ToList())
            {
                foreach (var item in deleted.Where(d => group.Items.Contains(d)).ToList())
                {
                    group.Items.Remove(item);
                }

                if (group.Items.Count <= 1)
                {
                    folderVm.Groups.Remove(group);
                }
            }

            if (folderVm.Groups.Count == 0)
            {
                FolderResults.Remove(folderVm);
            }
        }

        // Recomputed from whatever remains in the tree (rather than just zeroed), so any
        // items that failed to delete and are still checked keep counting correctly.
        Selection.Recompute(FolderResults.SelectMany(f => f.Groups).SelectMany(g => g.Items).Distinct());
        OnPropertyChanged(nameof(HasAnyResults));

        if (failures.Count == 0)
        {
            StatusMessage = $"Moved {deleted.Count} file(s) to the Recycle Bin.";
            MessageBox.Show(
                $"Moved {deleted.Count} file(s) ({MediaItemViewModel.FormatBytes(deleted.Sum(i => i.SizeBytes))}) to the Recycle Bin.",
                "Delete complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            StatusMessage = $"Moved {deleted.Count} file(s); {failures.Count} failed.";
            MessageBox.Show(
                $"Moved {deleted.Count} file(s) to the Recycle Bin.\n\n{failures.Count} file(s) could not be deleted:\n{string.Join("\n", failures)}",
                "Delete completed with errors", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

public sealed partial class FolderResultsViewModel : ObservableObject
{
    public string Path { get; }
    public int FileCount { get; }
    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = new();

    public event Action<bool>? ExpandedChanged;

    [ObservableProperty]
    private bool _isExpanded;

    // Which folder-wide filter (if any) is currently flattening this folder's Sets into
    // one result — "Thumb", "File", "Size", "Length", "Resolution", "Bit rate", "Audio",
    // "Match", "Hash", or null when showing the normal, detection-order Sets.
    [ObservableProperty]
    private string? _activeFolderFilter;

    private List<DuplicateGroupViewModel>? _originalGroupOrder;

    public FolderResultsViewModel(string path, int fileCount)
    {
        Path = path;
        FileCount = fileCount;
    }

    partial void OnIsExpandedChanged(bool value) => ExpandedChanged?.Invoke(value);

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    public void ApplyFlattenedOrder(IReadOnlyList<DuplicateGroupViewModel> ordered, string filterKey)
    {
        _originalGroupOrder ??= Groups.ToList();
        Groups.Clear();
        foreach (var group in ordered)
        {
            Groups.Add(group);
        }
        ActiveFolderFilter = filterKey;
    }

    public void RestoreOriginalGroupOrder()
    {
        if (_originalGroupOrder is null)
        {
            return;
        }

        Groups.Clear();
        foreach (var group in _originalGroupOrder)
        {
            Groups.Add(group);
        }
        ActiveFolderFilter = null;
    }
}
