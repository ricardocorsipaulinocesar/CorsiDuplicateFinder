using System.Numerics;

namespace CorsiDuplicate.Core.Hashing;

public static class HammingDistance
{
    public const int HashBits = 64;

    public static int Between(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    /// <summary>
    /// Minimum Hamming distance across every combination of the two items' orientation
    /// hash sets — this is what makes flipped/rotated copies compare as near-duplicates.
    /// </summary>
    public static int MinAcrossOrientations(IReadOnlyList<ulong> a, IReadOnlyList<ulong> b)
    {
        var min = int.MaxValue;
        foreach (var ha in a)
        {
            foreach (var hb in b)
            {
                var d = Between(ha, hb);
                if (d < min)
                {
                    min = d;
                }
            }
        }
        return min;
    }

    public static double ToSimilarityPercent(int distance) =>
        100.0 * (1.0 - (double)distance / HashBits);

    /// <summary>
    /// Compares two videos' sampled-keyframe hash sets. For each sampled frame of A,
    /// finds its own closest match among B's frames, then uses the *second*-closest of
    /// those per-frame results (rather than the single closest pair overall) as the
    /// video pair's similarity.
    ///
    /// A single best-matching pair across all frame x frame combinations sounds
    /// reasonable — it's what lets a trimmed copy still match on whatever content is
    /// shared — but with ~5 sampled frames per side that's effectively picking the
    /// luckiest result out of ~25 comparisons, and two entirely unrelated videos can
    /// easily produce one coincidentally-similar frame (a fade, a solid-color shot, a
    /// shared stock intro/outro) that alone was enough to score as a near-duplicate.
    /// Requiring at least two of A's sampled frames to each find a good match in B
    /// keeps the "a trim only drops content at the edges" tolerance (a genuine
    /// duplicate still shares most of its frames) while being far more resistant to
    /// that one-lucky-match false positive.
    /// </summary>
    public static double BestFramePairSimilarityPercent(
        IReadOnlyList<ulong[]> framesA, IReadOnlyList<ulong[]> framesB)
    {
        var perFrameBestDistances = new List<int>();
        foreach (var frameA in framesA)
        {
            var min = int.MaxValue;
            foreach (var frameB in framesB)
            {
                var d = MinAcrossOrientations(frameA, frameB);
                if (d < min)
                {
                    min = d;
                }
            }

            if (min != int.MaxValue)
            {
                perFrameBestDistances.Add(min);
            }
        }

        if (perFrameBestDistances.Count == 0)
        {
            return 0.0;
        }

        perFrameBestDistances.Sort();
        var distance = perFrameBestDistances.Count >= 2 ? perFrameBestDistances[1] : perFrameBestDistances[0];
        return ToSimilarityPercent(distance);
    }
}
