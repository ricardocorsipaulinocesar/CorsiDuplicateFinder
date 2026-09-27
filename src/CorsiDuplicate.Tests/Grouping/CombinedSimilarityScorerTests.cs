using CorsiDuplicate.Core.Grouping;
using CorsiDuplicate.Core.Matching;
using CorsiDuplicate.Core.Models;
using CorsiDuplicate.Core.Persons;

namespace CorsiDuplicate.Tests.Grouping;

public class CombinedSimilarityScorerTests
{
    private static MediaItem MakeItem(
        ulong[]? hashes = null, float[]? histogram = null, string sha256 = "unique", Guid? personClusterId = null) => new()
    {
        FullPath = @"C:\test\x.jpg",
        FileName = "x.jpg",
        Kind = MediaKind.Photo,
        SizeBytes = 1000,
        LastWriteUtc = DateTime.UtcNow,
        Sha256 = sha256 == "unique" ? Guid.NewGuid().ToString() : sha256,
        OrientationHashes = hashes,
        HsvHistogram = histogram,
        Faces = personClusterId is { } id
            ? new[] { new FaceDetection { Embedding = new float[] { 1f }, MatchedPersonClusterId = id } }
            : null
    };

    private sealed class StubStructuralMatcher : IStructuralMatcher
    {
        public double ScoreToReturn { get; set; }
        public int CallCount { get; private set; }

        public double Match(string imagePathA, string imagePathB)
        {
            CallCount++;
            return ScoreToReturn;
        }

        public void ClearCache() { }
    }

    [Fact]
    public void Exact_sha256_match_short_circuits_without_consulting_other_signals()
    {
        var a = MakeItem(sha256: "SAME");
        var b = MakeItem(sha256: "SAME");
        var structural = new StubStructuralMatcher { ScoreToReturn = 0 };

        var result = CombinedSimilarityScorer.Similarity(a, b, structuralMatcher: structural);

        Assert.Equal(100.0, result);
        Assert.Equal(0, structural.CallCount);
    }

    [Fact]
    public void Ambiguous_band_consults_structural_matcher_and_can_rescue_the_score()
    {
        // Orientation hashes ~50% similar (32-bit distance out of 64) land in the
        // ambiguous band, where a strong structural match should raise the score.
        var a = MakeItem(hashes: new ulong[] { 0UL });
        var b = MakeItem(hashes: new ulong[] { 0x00000000FFFFFFFFUL });
        var structural = new StubStructuralMatcher { ScoreToReturn = 95.0 };

        var result = CombinedSimilarityScorer.Similarity(a, b, structuralMatcher: structural);

        Assert.Equal(95.0, result);
        Assert.Equal(1, structural.CallCount);
    }

    [Fact]
    public void Confident_hash_match_skips_the_expensive_structural_check()
    {
        var a = MakeItem(hashes: new ulong[] { 0UL });
        var b = MakeItem(hashes: new ulong[] { 0UL }); // identical hash -> 100% already
        var structural = new StubStructuralMatcher { ScoreToReturn = 0 };

        var result = CombinedSimilarityScorer.Similarity(a, b, structuralMatcher: structural);

        Assert.Equal(100.0, result);
        Assert.Equal(0, structural.CallCount);
    }

    [Fact]
    public void Clearly_unrelated_hash_distance_skips_the_expensive_structural_check()
    {
        var a = MakeItem(hashes: new ulong[] { 0UL });
        var b = MakeItem(hashes: new ulong[] { ulong.MaxValue }); // 64-bit distance -> 0% similar
        var structural = new StubStructuralMatcher { ScoreToReturn = 99.0 };

        var result = CombinedSimilarityScorer.Similarity(a, b, structuralMatcher: structural);

        Assert.Equal(0.0, result);
        Assert.Equal(0, structural.CallCount);
    }

    [Fact]
    public void Shared_person_cluster_nudges_an_ambiguous_score_upward()
    {
        var personId = Guid.NewGuid();
        // 24-bit distance out of 64 -> 62.5% similar: plausible but not confident,
        // exactly the band the person-identity signal is meant to corroborate.
        var a = MakeItem(hashes: new ulong[] { 0UL }, personClusterId: personId);
        var b = MakeItem(hashes: new ulong[] { 0x0000000000FFFFFFUL }, personClusterId: personId);

        var withoutPerson = MakeItem(hashes: new ulong[] { 0UL });
        var bNoPerson = MakeItem(hashes: new ulong[] { 0x0000000000FFFFFFUL });
        var baseline = CombinedSimilarityScorer.Similarity(withoutPerson, bNoPerson);

        var result = CombinedSimilarityScorer.Similarity(a, b);

        Assert.True(result > baseline, $"expected shared person cluster to raise the score above baseline {baseline}, got {result}");
    }

    [Fact]
    public void Different_person_clusters_do_not_nudge_the_score()
    {
        var a = MakeItem(hashes: new ulong[] { 0UL }, personClusterId: Guid.NewGuid());
        var b = MakeItem(hashes: new ulong[] { 0x0000000000FFFFFFUL }, personClusterId: Guid.NewGuid());

        var withoutPerson = MakeItem(hashes: new ulong[] { 0UL });
        var bNoPerson = MakeItem(hashes: new ulong[] { 0x0000000000FFFFFFUL });
        var baseline = CombinedSimilarityScorer.Similarity(withoutPerson, bNoPerson);

        var result = CombinedSimilarityScorer.Similarity(a, b);

        Assert.Equal(baseline, result);
    }

    [Fact]
    public void Person_corroboration_never_pushes_the_score_to_a_confident_match()
    {
        var personId = Guid.NewGuid();
        var a = MakeItem(hashes: new ulong[] { 0UL }, personClusterId: personId);
        var b = MakeItem(hashes: new ulong[] { 0x0000000000FFFFFFUL }, personClusterId: personId);

        var result = CombinedSimilarityScorer.Similarity(a, b);

        Assert.True(result < 92.0, $"person corroboration should never reach confident-match territory, got {result}");
    }
}
