using System.Security.Cryptography;
using CorsiDuplicate.Core.Models;
using CorsiDuplicate.Core.Persons;
using CorsiDuplicate.Infrastructure.Logging;
using CorsiDuplicate.Infrastructure.Matching;
using CorsiDuplicate.Infrastructure.Media;
using CorsiDuplicate.Infrastructure.Persons;

namespace CorsiDuplicate.Infrastructure.Scanning;

public sealed record ScanProgress(int FilesDone, int FilesTotal, string Phase = "Scanning");

public sealed class ScanPipeline : IDisposable
{
    private readonly FolderScanner _scanner = new();
    private readonly ScanCacheStore _cache = new();
    private readonly ImageMetadataReader _imageReader = new();
    private readonly HsvHistogramComparer _histogramComparer = new();
    private readonly FfProbeMetadataReader _videoMetadataReader = new();
    private readonly FfmpegFrameExtractor _frameExtractor = new();
    private readonly OpenCvFaceRecognizer _faceRecognizer = new();
    private readonly HogPersonDetector _personDetector = new();
    private readonly SqlitePersonClusterStore _personClusterStore = new();
    private readonly VideoFingerprintCache _fingerprintCache = new();

    // Videos are only compared scene by scene when the cheap 5-frame check finds a plausible
    // look-alike. A best-of-25 frame pair of two unrelated videos already reaches ~62%, so the
    // bar sits well above that.
    private const double SceneCandidateFloorPercent = 72.0;

    public void Dispose()
    {
        _faceRecognizer.Dispose();
        _personClusterStore.Dispose();
    }

    public async Task<List<MediaItem>> ScanFolderAsync(
        string folderPath,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool videosOnly = false)
    {
        AppLogger.Info(nameof(ScanPipeline), nameof(ScanFolderAsync), $"Starting scan of '{folderPath}'{(videosOnly ? " (videos only)" : "")}.");

        // Enumeration + cache load are blocking disk I/O; running them on a
        // background thread keeps the UI thread's message pump (window resize,
        // minimize/maximize, input) responsive during a scan.
        var (files, cached) = await Task.Run(() =>
        {
            var f = _scanner.EnumerateMediaFiles(folderPath)
                .Where(file => !videosOnly || file.Kind == MediaKind.Video)
                .ToList();
            var c = _cache.Load(folderPath);
            return (f, c);
        }, cancellationToken);
        var collidingSizes = await Task.Run(() => FindCollidingSizes(files), cancellationToken);
        var results = new MediaItem?[files.Count];
        var done = 0;

        // Video extraction shells out to ffmpeg/ffprobe processes, which don't
        // benefit from (and would just contend under) the same parallelism used for
        // in-process photo hashing, so it runs at a smaller degree of concurrency.
        var photoIndices = new List<int>();
        var videoIndices = new List<int>();
        for (var i = 0; i < files.Count; i++)
        {
            (files[i].Kind == MediaKind.Photo ? photoIndices : videoIndices).Add(i);
        }

        await Parallel.ForEachAsync(
            photoIndices,
            new ParallelOptions
            {
                // Leave one core free so the UI thread isn't starved of CPU time
                // while this CPU-bound pass (OpenCV/ONNX) is running.
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
                CancellationToken = cancellationToken
            },
            async (i, ct) =>
            {
                var (path, kind) = files[i];
                results[i] = await ExtractOrLogAsync(
                    path, kind, cached, ct, _imageReader, _histogramComparer, _videoMetadataReader, _frameExtractor,
                    _faceRecognizer, _personDetector, _personClusterStore, folderPath, collidingSizes);
                var d = Interlocked.Increment(ref done);
                progress?.Report(new ScanProgress(d, files.Count));
            });

        await Parallel.ForEachAsync(
            videoIndices,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
                CancellationToken = cancellationToken
            },
            async (i, ct) =>
            {
                var (path, kind) = files[i];
                results[i] = await ExtractOrLogAsync(
                    path, kind, cached, ct, _imageReader, _histogramComparer, _videoMetadataReader, _frameExtractor,
                    _faceRecognizer, _personDetector, _personClusterStore, folderPath, collidingSizes);
                var d = Interlocked.Increment(ref done);
                progress?.Report(new ScanProgress(d, files.Count));
            });

        var items = results.Where(r => r is not null).Select(r => r!).ToList();
        await AttachSceneFingerprintsAsync(items, folderPath, progress, cancellationToken);
        // A videos-only scan keeps the folder's cached photo entries, so switching back to
        // "All" doesn't re-extract every photo.
        var keptPhotos = videosOnly
            ? cached.Values.Where(c => c.Kind == MediaKind.Photo).ToList()
            : new List<CachedMediaItem>();
        await Task.Run(() => _cache.Save(folderPath, items, keptPhotos), cancellationToken);
        AppLogger.Info(nameof(ScanPipeline), nameof(ScanFolderAsync), $"Finished scan of '{folderPath}': {items.Count}/{files.Count} file(s) extracted.");
        return items;
    }

    /// <summary>
    /// Wraps <see cref="ExtractAsync"/> so a bug or a corrupt/unreadable file crashing
    /// the extraction of ONE item logs the failure and is skipped (already tolerated
    /// downstream via the null-filtering in <see cref="ScanFolderAsync"/>), instead of
    /// aborting the scan of every other file in the folder.
    /// </summary>
    private static async Task<MediaItem?> ExtractOrLogAsync(
        string path, MediaKind kind, Dictionary<string, CachedMediaItem> cached, CancellationToken ct,
        ImageMetadataReader imageReader, HsvHistogramComparer histogramComparer,
        FfProbeMetadataReader videoMetadataReader, FfmpegFrameExtractor frameExtractor,
        OpenCvFaceRecognizer faceRecognizer, HogPersonDetector personDetector,
        SqlitePersonClusterStore personClusterStore, string folderPath, HashSet<long> collidingSizes)
    {
        try
        {
            return await ExtractAsync(
                path, kind, cached, ct, imageReader, histogramComparer, videoMetadataReader, frameExtractor,
                faceRecognizer, personDetector, personClusterStore, folderPath, collidingSizes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Error(nameof(ScanPipeline), nameof(ExtractOrLogAsync), $"Failed to extract signals for '{path}'.", ex);
            return null;
        }
    }

    private static async Task<MediaItem?> ExtractAsync(
        string path, MediaKind kind, Dictionary<string, CachedMediaItem> cached, CancellationToken ct,
        ImageMetadataReader imageReader, HsvHistogramComparer histogramComparer,
        FfProbeMetadataReader videoMetadataReader, FfmpegFrameExtractor frameExtractor,
        OpenCvFaceRecognizer faceRecognizer, HogPersonDetector personDetector,
        SqlitePersonClusterStore personClusterStore, string folderPath, HashSet<long> collidingSizes)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }
        }
        catch (IOException)
        {
            return null;
        }

        var lastWriteUtc = info.LastWriteTimeUtc;
        var key = ScanCacheStore.CacheKey(path, info.Length, lastWriteUtc);

        if (cached.TryGetValue(key, out var hit))
        {
            var cachedItem = hit.ToMediaItem();
            if (cachedItem.HasQuickHash && collidingSizes.Contains(info.Length)
                && await TryFullHashAsync(path, ct) is { } fullHash)
            {
                // Another file of the same size has appeared since this one was cached.
                cachedItem.Sha256 = fullHash;
            }

            // The person-cluster library keeps learning every scan, even for
            // otherwise-unchanged files, using the embeddings already cached —
            // no need to re-run the (expensive) face/body detection itself.
            ResolveClusters(cachedItem, folderPath, personClusterStore);
            return cachedItem;
        }

        // Only a file that shares its size with another one in the folder can be a byte-for-byte
        // copy, so only those are read in full; the rest get a quick hash of their ends.
        var sha256 = collidingSizes.Contains(info.Length)
            ? await TryFullHashAsync(path, ct)
            : await TryQuickHashAsync(path, info.Length, ct);
        if (sha256 is null)
        {
            return null;
        }

        var width = 0;
        var height = 0;
        ulong[]? orientationHashes = null;
        float[]? histogram = null;
        string? codec = null;
        long? bitRateBps = null;
        TimeSpan? duration = null;
        int? audioSampleRateHz = null;
        long? audioBitRateBps = null;
        IReadOnlyList<ulong[]>? frameOrientationHashes = null;
        List<FaceDetection>? faces = null;
        List<PersonDetection>? bodies = null;

        if (kind == MediaKind.Photo)
        {
            var signals = imageReader.TryRead(path);
            if (signals is not null)
            {
                width = signals.Width;
                height = signals.Height;
                orientationHashes = signals.OrientationHashes;
            }

            try
            {
                var computed = histogramComparer.ComputeHistogram(path);
                histogram = computed.Length > 0 ? computed : null;
            }
            catch (Exception ex) when (ex is IOException or OpenCvSharp.OpenCVException)
            {
                histogram = null;
            }

            try
            {
                var detectedFaces = faceRecognizer.DetectFaces(path);
                if (detectedFaces.Count > 0)
                {
                    faces = detectedFaces.ToList();
                }
                else
                {
                    // Fallback signal only when no face is visible at all.
                    var detectedBodies = personDetector.DetectPersons(path);
                    if (detectedBodies.Count > 0)
                    {
                        bodies = detectedBodies.ToList();
                    }
                }
            }
            catch (Exception)
            {
                // Person recognition is a corroborating signal, not a required one —
                // never let a detection failure fail the whole scan.
            }
        }
        else
        {
            var metadata = await videoMetadataReader.TryReadAsync(path, ct);
            if (metadata is not null)
            {
                width = metadata.Width;
                height = metadata.Height;
                codec = metadata.Codec;
                bitRateBps = metadata.BitRateBps;
                duration = metadata.Duration;
                audioSampleRateHz = metadata.AudioSampleRateHz;
                audioBitRateBps = metadata.AudioBitRateBps;

                var videoSignals = await frameExtractor.ExtractAsync(path, metadata.Duration, ct);
                if (videoSignals is not null)
                {
                    frameOrientationHashes = videoSignals.FrameOrientationHashes;
                    histogram = videoSignals.RepresentativeHistogram;
                }
            }
        }

        var item = new MediaItem
        {
            FullPath = path,
            FileName = Path.GetFileName(path),
            Kind = kind,
            SizeBytes = info.Length,
            LastWriteUtc = lastWriteUtc,
            Sha256 = sha256,
            Width = width,
            Height = height,
            Codec = codec,
            BitRateBps = bitRateBps,
            Duration = duration,
            AudioSampleRateHz = audioSampleRateHz,
            AudioBitRateBps = audioBitRateBps,
            OrientationHashes = orientationHashes,
            FrameOrientationHashes = frameOrientationHashes,
            HsvHistogram = histogram,
            Faces = faces,
            Bodies = bodies
        };

        ResolveClusters(item, folderPath, personClusterStore);
        return item;
    }

    private static HashSet<long> FindCollidingSizes(IEnumerable<(string Path, MediaKind Kind)> files)
    {
        var seen = new HashSet<long>();
        var colliding = new HashSet<long>();
        foreach (var (path, _) in files)
        {
            try
            {
                var length = new FileInfo(path).Length;
                if (!seen.Add(length))
                {
                    colliding.Add(length);
                }
            }
            catch (IOException)
            {
                // Unreadable now; the extraction step skips it too.
            }
        }
        return colliding;
    }

    private static async Task<string?> TryFullHashAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>"Q" + 63 hex characters from the file size and its first and last 64 KB. "Q" is not
    /// a hex digit, so a quick hash can never equal a full SHA-256.</summary>
    private static async Task<string?> TryQuickHashAsync(string path, long length, CancellationToken ct)
    {
        const int chunk = 64 * 1024;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(BitConverter.GetBytes(length));
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            var buffer = new byte[chunk];

            var read = await stream.ReadAsync(buffer.AsMemory(), ct);
            hash.AppendData(buffer, 0, read);
            if (length > chunk)
            {
                stream.Position = Math.Max(chunk, length - chunk);
                read = await stream.ReadAsync(buffer.AsMemory(), ct);
                hash.AppendData(buffer, 0, read);
            }
            return "Q" + Convert.ToHexString(hash.GetHashAndReset())[..63];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gives every video that might have a look-alike its scene sequence (one hash per second,
    /// cached on disk) so grouping judges videos by their scenes, not by one lucky frame. The
    /// cheap 5-frame check nominates the candidates; all other videos are marked as checked
    /// without a sequence, meaning "nothing like them here".
    /// </summary>
    private async Task AttachSceneFingerprintsAsync(
        List<MediaItem> items, string folderPath, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var videos = items.Where(i => i.Kind == MediaKind.Video && i.FrameOrientationHashes is { Count: > 0 }).ToList();
        if (videos.Count < 2)
        {
            foreach (var video in videos)
            {
                video.SceneCheckDone = true;
            }
            return;
        }

        var nominated = new bool[videos.Count];
        await Task.Run(() => Parallel.For(0, videos.Count, new ParallelOptions { CancellationToken = ct }, a =>
        {
            for (var b = a + 1; b < videos.Count; b++)
            {
                if (nominated[a] && nominated[b])
                {
                    continue;
                }

                // Exact copies are already 100% by hash; no scenes to compare.
                if (videos[a].Sha256 != videos[b].Sha256
                    && Core.Hashing.HammingDistance.BestFramePairSimilarityPercent(
                        videos[a].FrameOrientationHashes!, videos[b].FrameOrientationHashes!) >= SceneCandidateFloorPercent)
                {
                    nominated[a] = true;
                    nominated[b] = true;
                }
            }
        }), ct);

        var candidates = Enumerable.Range(0, videos.Count).Where(i => nominated[i]).Select(i => videos[i]).ToList();
        AppLogger.Info(nameof(ScanPipeline), nameof(AttachSceneFingerprintsAsync),
            $"'{folderPath}': {candidates.Count} of {videos.Count} video(s) need a scene comparison.");

        var done = 0;
        await Parallel.ForEachAsync(candidates,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4), CancellationToken = ct },
            async (video, token) =>
            {
                try
                {
                    video.SceneFingerprint = await _fingerprintCache.GetOrCreateAsync(video, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AppLogger.Error(nameof(ScanPipeline), nameof(AttachSceneFingerprintsAsync),
                        $"Could not fingerprint '{video.FullPath}'.", ex);
                }

                progress?.Report(new ScanProgress(Interlocked.Increment(ref done), candidates.Count, "Comparing video scenes"));
            });

        foreach (var video in videos)
        {
            video.SceneCheckDone = true;
        }
    }

    private static void ResolveClusters(MediaItem item, string folderPath, SqlitePersonClusterStore store)
    {
        if (item.Faces is not null)
        {
            foreach (var face in item.Faces)
            {
                face.MatchedPersonClusterId = store.MatchOrCreateCluster(folderPath, face.Embedding);
            }
        }
        else if (item.Bodies is not null)
        {
            foreach (var body in item.Bodies)
            {
                // Bodies use a looser threshold since the histogram-based embedding is
                // a coarser signal than a face embedding.
                body.MatchedPersonClusterId = store.MatchOrCreateCluster(folderPath, body.Embedding, similarityThreshold: 0.8);
            }
        }
    }
}
