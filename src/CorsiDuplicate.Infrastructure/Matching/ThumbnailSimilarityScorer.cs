using CorsiDuplicate.Core.Hashing;

namespace CorsiDuplicate.Infrastructure.Matching;

/// <summary>
/// Detailed similarity score between two already-generated thumbnail images (the same
/// small JPEG shown in the results grid, for both photos and videos), combining
/// orientation-invariant perceptual hashing, HSV color histogram correlation, and ORB
/// structural keypoint matching — the same three signals the app's main duplicate
/// detection pipeline uses, applied here only to the thumbnail image itself rather than
/// full-resolution frames, so sorting stays fast while still being criterious.
/// </summary>
public sealed class ThumbnailSimilarityScorer
{
    // Outside this band, the cheap signals (hash/histogram) already agree clearly
    // enough that spending ORB's much higher cost wouldn't change the ranking.
    private const double OrbRescueFloor = 45.0;
    private const double OrbRescueCeiling = 97.0;

    private readonly OrientationInvariantHasher _hasher = new();
    private readonly HsvHistogramComparer _histogramComparer = new();
    private readonly OrbStructuralMatcher _structuralMatcher = new();

    private readonly Dictionary<string, ulong[]> _hashCache = new();
    private readonly Dictionary<string, float[]> _histogramCache = new();

    public double Compare(string thumbnailPathA, string thumbnailPathB)
    {
        if (string.Equals(thumbnailPathA, thumbnailPathB, StringComparison.OrdinalIgnoreCase))
        {
            return 100.0;
        }

        var hashesA = GetHashes(thumbnailPathA);
        var hashesB = GetHashes(thumbnailPathB);
        var hashSimilarity = hashesA.Length > 0 && hashesB.Length > 0
            ? HammingDistance.ToSimilarityPercent(HammingDistance.MinAcrossOrientations(hashesA, hashesB))
            : 0.0;

        var histogramA = GetHistogram(thumbnailPathA);
        var histogramB = GetHistogram(thumbnailPathB);
        var histogramSimilarity = histogramA.Length > 0 && histogramB.Length > 0
            ? _histogramComparer.Compare(histogramA, histogramB) * 100.0
            : 0.0;

        var combined = Math.Max(hashSimilarity, histogramSimilarity * 0.9);

        // Cheap signals alone can't be trusted in the ambiguous middle band, so spend
        // the expensive-but-precise structural check to settle it.
        if (combined is > OrbRescueFloor and < OrbRescueCeiling)
        {
            var structural = _structuralMatcher.Match(thumbnailPathA, thumbnailPathB);
            if (structural > 0)
            {
                combined = Math.Max(combined, structural);
            }
        }

        return Math.Clamp(combined, 0.0, 100.0);
    }

    private ulong[] GetHashes(string path)
    {
        if (_hashCache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        ulong[] hashes;
        try
        {
            hashes = _hasher.HashAllOrientations(path);
        }
        catch (Exception)
        {
            hashes = Array.Empty<ulong>();
        }

        _hashCache[path] = hashes;
        return hashes;
    }

    private float[] GetHistogram(string path)
    {
        if (_histogramCache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        float[] histogram;
        try
        {
            histogram = _histogramComparer.ComputeHistogram(path);
        }
        catch (Exception)
        {
            histogram = Array.Empty<float>();
        }

        _histogramCache[path] = histogram;
        return histogram;
    }
}
