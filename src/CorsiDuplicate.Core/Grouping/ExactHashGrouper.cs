using CorsiDuplicate.Core.Models;

namespace CorsiDuplicate.Core.Grouping;

/// <summary>
/// Phase 1 grouping strategy: clusters items whose SHA-256 matches exactly.
/// Superseded by HammingClusterGrouper (perceptual similarity) in a later phase,
/// but SHA-256 stays as the always-first, zero-cost exact-match short-circuit.
/// </summary>
public sealed class ExactHashGrouper : IGroupingStrategy
{
    public List<DuplicateGroup> Group(IReadOnlyList<MediaItem> items, double threshold)
    {
        var groups = new List<DuplicateGroup>();

        foreach (var bucket in items.GroupBy(i => i.Sha256))
        {
            var bucketItems = bucket.ToList();
            if (bucketItems.Count < 2)
            {
                continue;
            }

            var group = new DuplicateGroup();
            group.Items.AddRange(bucketItems);
            group.Reference = bucketItems[0];

            foreach (var item in bucketItems)
            {
                item.SimilarityToReferencePercent = 100.0;
            }

            groups.Add(group);
        }

        return groups;
    }
}
