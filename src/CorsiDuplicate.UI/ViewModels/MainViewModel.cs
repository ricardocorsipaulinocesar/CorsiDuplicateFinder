using System.Collections.ObjectModel;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CorsiDuplicate.Core.Abstractions;
using CorsiDuplicate.Core.Grouping;
using CorsiDuplicate.Core.Models;
using CorsiDuplicate.Infrastructure.IO;
using CorsiDuplicate.Infrastructure.Logging;
using CorsiDuplicate.Infrastructure.Matching;
using CorsiDuplicate.Infrastructure.Scanning;
using Microsoft.Win32;

namespace CorsiDuplicate.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ScanPipeline _scanPipeline = new();
    private readonly OrbStructuralMatcher _structuralMatcher = new();
    private readonly IGroupingStrategy _grouper;
    private readonly IRecycleBinService _recycleBin = new RecycleBinService();

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

    [ObservableProperty]
    private double _thumbnailColumnWidth = 120;

    [ObservableProperty]
    private int _scanProgressCurrent;

    [ObservableProperty]
    private int _scanProgressTotal;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    public bool HasAnyResults => FolderResults.Count > 0;

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
            Selection.Recompute(FolderResults.SelectMany(f => f.Groups).SelectMany(g => g.Items));
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

                StatusMessage = $"Enumerating files in {folder.Path}...";
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
                        StatusMessage = $"Scanning {folder.Path}... {p.FilesDone}/{p.FilesTotal}{FormatEta(p)}";
                    });

                    var items = await _scanPipeline.ScanFolderAsync(folder.Path, progress, _scanCts.Token);
                    FolderManager.RecordScanResult(folder.Path, items.Count);
                    _scannedItemsByFolder[folder.Path] = items;

                    // Results appear per folder as each one finishes, instead of waiting
                    // for every enabled folder to complete before showing anything.
                    await RebuildResultsFromMemoryAsync();
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
                    StatusMessage = $"Error scanning {folder.Path}: {ex.Message}";
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

            if (cancelled)
            {
                StatusMessage = "Scan stopped.";
                AppLogger.Info(nameof(MainViewModel), nameof(ScanAsync), "Scan stopped by user.");
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

        return $" — about {display} remaining";
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
            .ToDictionary(i => i.Model.FullPath);

        var snapshot = _scannedItemsByFolder
            .Select(kv => (kv.Key, Items: kv.Value.ToList()))
            .ToList();
        var threshold = SimilarityThreshold;

        List<(string FolderPath, List<DuplicateGroup> Groups, int ItemCount)> perFolder;
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
        }

        if (myVersion != _regroupRequestVersion)
        {
            // Stale by the time grouping finished — a newer request will apply its own results.
            return;
        }

        FolderResults.Clear();

        foreach (var (folderPath, groups, itemCount) in perFolder)
        {
            if (groups.Count == 0)
            {
                continue;
            }

            if (!_folderExpandState.TryGetValue(folderPath, out var isExpanded))
            {
                // First folder to ever produce results starts expanded; every one after starts collapsed.
                isExpanded = _folderExpandState.Count == 0;
                _folderExpandState[folderPath] = isExpanded;
            }

            var folderVm = new FolderResultsViewModel(folderPath, itemCount) { IsExpanded = isExpanded };
            folderVm.ExpandedChanged += expanded => _folderExpandState[folderPath] = expanded;

            var setNumber = 1;
            foreach (var group in groups)
            {
                var itemVms = group.Items
                    .Select(i => existingByPath.TryGetValue(i.FullPath, out var existing)
                        ? existing
                        : CreateItemViewModel(i, folderPath))
                    .ToList();
                folderVm.Groups.Add(new DuplicateGroupViewModel(group, folderPath, itemVms) { SetNumber = setNumber++ });
            }
            FolderResults.Add(folderVm);
        }

        Selection.Recompute(FolderResults.SelectMany(f => f.Groups).SelectMany(g => g.Items));
        OnPropertyChanged(nameof(HasAnyResults));
    }

    private MediaItemViewModel CreateItemViewModel(MediaItem item, string folderPath)
    {
        var vm = new MediaItemViewModel(item, folderPath);
        vm.SelectionChanged += _ => Selection.OnSelectionChanged(vm);
        vm.PlayRequested += OnPlayRequested;
        return vm;
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
        var allItems = FolderResults.SelectMany(f => f.Groups).SelectMany(g => g.Items).ToList();
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
        Selection.Recompute(FolderResults.SelectMany(f => f.Groups).SelectMany(g => g.Items));
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

    public FolderResultsViewModel(string path, int fileCount)
    {
        Path = path;
        FileCount = fileCount;
    }

    partial void OnIsExpandedChanged(bool value) => ExpandedChanged?.Invoke(value);

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}
