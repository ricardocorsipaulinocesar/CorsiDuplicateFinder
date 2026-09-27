using CorsiDuplicate.Core.Hashing;
using CorsiDuplicate.Core.Matching;
using CorsiDuplicate.Core.Models;

namespace CorsiDuplicate.Core.Grouping;

/// <summary>
/// Blends every available duplicate-detection signal into one similarity percentage:
/// exact SHA-256 match short-circuits to 100%; otherwise the orientation-invariant
/// perceptual hash distance is the primary (cheap, fast) signal, the HSV histogram
/// distance is a secondary signal that catches color/exposure edits hashing misses,
/// and — only when those two leave the pair ambiguous (not confidently a match, not
/// confidently unrelated) — an ORB structural match is run to rescue crop/border
/// cases that change the image's global layout enough to fool both cheaper signals.
/// Recognized-person identity (face embedding preferred, body embedding as a weaker
/// fallback) is folded in last, purely as a corroborating nudge: two unrelated photos
/// of the same person at different events shouldn't become "duplicates" just because
/// they share a face, so this only raises a score that's already in a plausible band,
/// never on its own.
/// </summary>
public static class CombinedSimilarityScorer
{
    /// <summary>Below this, cheap signals already agree the pair is unrelated; skip ORB entirely.</summary>
    private const double StructuralCheckFloor = 35.0;

    /// <summary>Above this, cheap signals already agree the pair is a confident match; skip ORB entirely.</summary>
    private const double StructuralCheckCeiling = 92.0;

    /// <summary>
    /// A random, wholly unrelated 64-bit orientation hash pair still lands around 50%
    /// similarity by pure chance, so the histogram rescue floor must sit above that
    /// baseline — otherwise it fires for almost every pair, not just genuinely ambiguous
    /// ones, which is what let two visually unrelated photos/videos with a merely similar
    /// color palette (a histogram is spatial-layout-blind) get scored as near-duplicates.
    /// </summary>
    private const double HistogramRescueFloor = 60.0;

    /// <summary>
    /// The histogram may only nudge a pair the hash signal already finds plausible, never
    /// stand in for it — its raw score is capped to at most this much above the hash
    /// score, so a color-palette match alone can never carry an unrelated pair to "confident duplicate".
    /// </summary>
    private const double HistogramBoostCap = 20.0;

    /// <summary>A shared person only nudges scores already at least this plausible.</summary>
    private const double PersonCorroborationFloor = 40.0;

    /// <summary>The nudge never pushes the score above this — same person isn't the same photo.</summary>
    private const double PersonCorroborationCeiling = 90.0;

    private const double PersonCorroborationBoost = 10.0;

    public static double Similarity(
        MediaItem a,
        MediaItem b,
        IHistogramComparer? histogramComparer = null,
        IStructuralMatcher? structuralMatcher = null)
    {
        if (a.Sha256 == b.Sha256)
        {
            return 100.0;
        }

        if (a.Kind != b.Kind)
        {
            return 0.0;
        }

        var hashSimilarity = 0.0;
        if (a.OrientationHashes is { Length: > 0 } ha && b.OrientationHashes is { Length: > 0 } hb)
        {
            // Photos: compare the single-frame orientation hash sets directly.
            var distance = HammingDistance.MinAcrossOrientations(ha, hb);
            hashSimilarity = HammingDistance.ToSimilarityPercent(distance);
        }
        else if (a.FrameOrientationHashes is { Count: > 0 } fa && b.FrameOrientationHashes is { Count: > 0 } fb)
        {
            // Videos: the best-matching pair of sampled keyframes decides the score,
            // so a trimmed/partially re-graded copy can still match on one shared frame.
            hashSimilarity = HammingDistance.BestFramePairSimilarityPercent(fa, fb);
        }

        var best = hashSimilarity;

        // The histogram ignores spatial layout entirely, so two genuinely different
        // photos/videos that merely share a color palette can score deceptively high on
        // it alone. It may only rescue a pair the hash signal already finds plausible
        // (never one it considers unrelated), and its contribution is capped relative to
        // the hash score rather than trusted at face value.
        //
        // For photos the histogram covers the WHOLE image, so it's a reasonably strong
        // signal. For a video it's only ONE sampled frame standing in for the entire
        // clip's color identity — far weaker, and for content where most clips share the
        // same performer/set/lighting (so most frames are skin-tone/set-color dominated
        // regardless of which clip it is), that one frame's palette correlates with
        // nearly every other clip's, letting genuinely unrelated videos get "rescued"
        // into a near-100% score. So it's restricted to photos only, same as ORB below.
        if (histogramComparer is not null && a.Kind == MediaKind.Photo
            && a.HsvHistogram is { Length: > 0 } histA && b.HsvHistogram is { Length: > 0 } histB
            && hashSimilarity is > HistogramRescueFloor and < StructuralCheckCeiling)
        {
            var histogramSimilarity = Math.Clamp(histogramComparer.Compare(histA, histB), 0.0, 1.0) * 100.0;
            best = Math.Max(best, Math.Min(histogramSimilarity, hashSimilarity + HistogramBoostCap));
        }

        // ORB structural matching operates on a single decoded image; for now it only
        // helps rescue ambiguous photo pairs, not multi-frame video pairs.
        if (structuralMatcher is not null && a.Kind == MediaKind.Photo && hashSimilarity is > StructuralCheckFloor and < StructuralCheckCeiling)
        {
            var structuralScore = structuralMatcher.Match(a.FullPath, b.FullPath);
            best = Math.Max(best, structuralScore);
        }

        if (best is > PersonCorroborationFloor and < PersonCorroborationCeiling && SharePersonCluster(a, b))
        {
            best = Math.Min(best + PersonCorroborationBoost, PersonCorroborationCeiling);
        }

        return best;
    }

    private static bool SharePersonCluster(MediaItem a, MediaItem b)
    {
        var aClusters = ClusterIds(a);
        if (aClusters.Count == 0)
        {
            return false;
        }

        var bClusters = ClusterIds(b);
        return aClusters.Overlaps(bClusters);
    }

    private static HashSet<Guid> ClusterIds(MediaItem item)
    {
        var ids = new HashSet<Guid>();
        if (item.Faces is not null)
        {
            foreach (var face in item.Faces)
            {
                if (face.MatchedPersonClusterId is { } id)
                {
                    ids.Add(id);
                }
            }
        }

        if (ids.Count == 0 && item.Bodies is not null)
        {
            foreach (var body in item.Bodies)
            {
                if (body.MatchedPersonClusterId is { } id)
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }
}
