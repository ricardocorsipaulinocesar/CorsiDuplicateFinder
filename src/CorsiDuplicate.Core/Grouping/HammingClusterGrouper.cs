using CorsiDuplicate.Core.Matching;
using CorsiDuplicate.Core.Models;

namespace CorsiDuplicate.Core.Grouping;

/// <summary>
/// Pure, folder-agnostic clustering: for each pair of items, CombinedSimilarityScorer
/// blends exact-hash, orientation-invariant perceptual hash, HSV histogram, and (for
/// ambiguous pairs) ORB structural matching into one similarity percentage.
///
/// Grouping uses "leader" (star) clustering rather than transitive union-find: items
/// are considered in quality order, the best-quality unassigned item becomes a group's
/// reference, and every other unassigned item whose similarity *to that reference*
/// meets the threshold joins the group. This guarantees every member's displayed match
/// percentage is always >= the slider threshold, and avoids the "chained" clustering
/// artifact where A~B and B~C get grouped even though A and C aren't actually alike —
/// union-find would merge them transitively through B; leader clustering won't, since
/// it only checks similarity to the group's own reference.
///
/// Called once per folder today — that's what enforces "no cross-folder matching" for
/// now, not this algorithm, so a future cross-folder mode can call this on a flattened
/// item list with no rework.
/// </summary>
public sealed class HammingClusterGrouper : IGroupingStrategy
{
    private readonly IHistogramComparer? _histogramComparer;
    private readonly IStructuralMatcher? _structuralMatcher;

    public HammingClusterGrouper(IHistogramComparer? histogramComparer = null, IStructuralMatcher? structuralMatcher = null)
    {
        _histogramComparer = histogramComparer;
        _structuralMatcher = structuralMatcher;
    }

    public List<DuplicateGroup> Group(IReadOnlyList<MediaItem> items, double thresholdPercent)
    {
        _structuralMatcher?.ClearCache();

        // Best quality first, so the reference of every group is its best copy.
        var order = Enumerable.Range(0, items.Count)
            .OrderByDescending(i => QualityScorer.Score(items[i]))
            .ToList();

        var assigned = new bool[items.Count];
        var groups = new List<DuplicateGroup>();

        foreach (var refIndex in order)
        {
            if (assigned[refIndex])
            {
                continue;
            }

            var reference = items[refIndex];
            var memberIndices = new List<int> { refIndex };

            foreach (var candidateIndex in order)
            {
                if (candidateIndex == refIndex || assigned[candidateIndex])
                {
                    continue;
                }

                if (Similarity(items[candidateIndex], reference) >= thresholdPercent)
                {
                    memberIndices.Add(candidateIndex);
                }
            }

            if (memberIndices.Count < 2)
            {
                // Not marking `assigned` here: a lone item might still end up as a
                // member of a later (lower-quality) reference's group. Leaving it
                // unassigned lets that happen; if it never matches anything, it's
                // simply never emitted as a group.
                continue;
            }

            foreach (var i in memberIndices)
            {
                assigned[i] = true;
                items[i].SimilarityToReferencePercent = ReferenceEquals(items[i], reference)
                    ? 100.0
                    : Similarity(items[i], reference);
            }

            var group = new DuplicateGroup { Reference = reference };
            group.Items.AddRange(memberIndices.Select(i => items[i]));
            groups.Add(group);
        }

        return groups;
    }

    private double Similarity(MediaItem a, MediaItem b) =>
        CombinedSimilarityScorer.Similarity(a, b, _histogramComparer, _structuralMatcher);
}
