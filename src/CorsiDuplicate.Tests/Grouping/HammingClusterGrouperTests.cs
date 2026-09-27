using CorsiDuplicate.Core.Grouping;
using CorsiDuplicate.Core.Models;

namespace CorsiDuplicate.Tests.Grouping;

public class HammingClusterGrouperTests
{
    private static MediaItem MakeItem(string name, ulong[] orientationHashes, string sha256 = "unique") => new()
    {
        FullPath = $@"C:\test\{name}",
        FileName = name,
        Kind = MediaKind.Photo,
        SizeBytes = 1000,
        LastWriteUtc = DateTime.UtcNow,
        Sha256 = sha256 == "unique" ? Guid.NewGuid().ToString() : sha256,
        OrientationHashes = orientationHashes
    };

    [Fact]
    public void Clusters_items_within_threshold_and_ignores_far_apart_items()
    {
        var a = MakeItem("a.jpg", new ulong[] { 0b0000_0000 });
        // b differs from a by 2 bits -> ~96.9% similar
        var b = MakeItem("b.jpg", new ulong[] { 0b0000_0011 });
        // c differs from a by 40 bits -> far below any reasonable threshold
        var c = MakeItem("c.jpg", new ulong[] { 0xFFFFFFFFFFUL });

        var grouper = new HammingClusterGrouper();
        var groups = grouper.Group(new[] { a, b, c }, thresholdPercent: 90);

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Items.Count);
        Assert.Contains(groups[0].Items, i => i.FileName == "a.jpg");
        Assert.Contains(groups[0].Items, i => i.FileName == "b.jpg");
    }

    [Fact]
    public void Raising_threshold_splits_a_previously_matched_pair()
    {
        var a = MakeItem("a.jpg", new ulong[] { 0b0000_0000 });
        var b = MakeItem("b.jpg", new ulong[] { 0b0000_0011 }); // 2-bit difference, 96.9% similar

        var grouper = new HammingClusterGrouper();

        var looseGroups = grouper.Group(new[] { a, b }, thresholdPercent: 90);
        Assert.Single(looseGroups);

        var strictGroups = grouper.Group(new[] { a, b }, thresholdPercent: 99);
        Assert.Empty(strictGroups);
    }

    [Fact]
    public void Exact_sha256_match_always_groups_regardless_of_hash_distance()
    {
        var a = MakeItem("a.jpg", new ulong[] { 0UL }, sha256: "SAME");
        var b = MakeItem("b.jpg", new ulong[] { ulong.MaxValue }, sha256: "SAME");

        var grouper = new HammingClusterGrouper();
        var groups = grouper.Group(new[] { a, b }, thresholdPercent: 99.9);

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Items.Count);
    }

    [Fact]
    public void Does_not_chain_unrelated_items_through_a_shared_middle_neighbor()
    {
        // a~b are close (distance 4), b~c are close (distance 4), but a~c are far
        // apart (distance 8) — below the 90% threshold on their own. Old union-find
        // clustering would transitively chain a-b-c into one group even though a and
        // c aren't really alike; leader clustering must not do that.
        var a = MakeItem("a.jpg", new ulong[] { 0b0000_0000_0000 });       // 0
        var b = MakeItem("b.jpg", new ulong[] { 0b0000_0000_1111 });       // distance 4 from a
        var c = MakeItem("c.jpg", new ulong[] { 0b1111_1111_0000 });       // distance 8 from a, distance 4 from b

        var grouper = new HammingClusterGrouper();
        // 64-bit hash space: distance 4 -> 93.75% similar (passes 90%), distance 8 -> 87.5% (fails 90%).
        var groups = grouper.Group(new[] { a, b, c }, thresholdPercent: 90);

        // a is the best-quality reference (first in insertion/quality order here since
        // all have equal quality scores, ties keep stable order): its group should
        // only include b (within threshold), not c.
        Assert.Single(groups);
        var group = groups[0];
        Assert.True(group.Items.All(i => i.SimilarityToReferencePercent >= 90.0),
            "every group member's match to the reference must be at or above the threshold");
    }
}
