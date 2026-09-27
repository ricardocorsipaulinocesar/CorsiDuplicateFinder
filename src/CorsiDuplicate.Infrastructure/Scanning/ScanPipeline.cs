using System.Security.Cryptography;
using CorsiDuplicate.Core.Models;
using CorsiDuplicate.Core.Persons;
using CorsiDuplicate.Infrastructure.Logging;
using CorsiDuplicate.Infrastructure.Matching;
using CorsiDuplicate.Infrastructure.Media;
using CorsiDuplicate.Infrastructure.Persons;

namespace CorsiDuplicate.Infrastructure.Scanning;

public sealed record ScanProgress(int FilesDone, int FilesTotal);

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

    public void Dispose()
    {
        _faceRecognizer.Dispose();
        _personClusterStore.Dispose();
    }

    public async Task<List<MediaItem>> ScanFolderAsync(
        string folderPath,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        AppLogger.Info(nameof(ScanPipeline), nameof(ScanFolderAsync), $"Starting scan of '{folderPath}'.");

        // Enumeration + cache load are blocking disk I/O; running them on a
        // background thread keeps the UI thread's message pump (window resize,
        // minimize/maximize, input) responsive during a scan.
        var (files, cached) = await Task.Run(() =>
        {
            var f = _scanner.EnumerateMediaFiles(folderPath).ToList();
            var c = _cache.Load(folderPath);
            return (f, c);
        }, cancellationToken);
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
                    _faceRecognizer, _personDetector, _personClusterStore, folderPath);
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
                    _faceRecognizer, _personDetector, _personClusterStore, folderPath);
                var d = Interlocked.Increment(ref done);
                progress?.Report(new ScanProgress(d, files.Count));
            });

        var items = results.Where(r => r is not null).Select(r => r!).ToList();
        await Task.Run(() => _cache.Save(folderPath, items), cancellationToken);
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
        SqlitePersonClusterStore personClusterStore, string folderPath)
    {
        try
        {
            return await ExtractAsync(
                path, kind, cached, ct, imageReader, histogramComparer, videoMetadataReader, frameExtractor,
                faceRecognizer, personDetector, personClusterStore, folderPath);
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
        SqlitePersonClusterStore personClusterStore, string folderPath)
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
            // The person-cluster library keeps learning every scan, even for
            // otherwise-unchanged files, using the embeddings already cached —
            // no need to re-run the (expensive) face/body detection itself.
            ResolveClusters(cachedItem, folderPath, personClusterStore);
            return cachedItem;
        }

        string sha256;
        try
        {
            await using var stream = File.OpenRead(path);
            var hashBytes = await SHA256.HashDataAsync(stream, ct);
            sha256 = Convert.ToHexString(hashBytes);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
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
