using CorsiDuplicate.Core.Hashing;

namespace CorsiDuplicate.Tests.Hashing;

public class HammingDistanceTests
{
    [Fact]
    public void Identical_hashes_have_zero_distance()
    {
        Assert.Equal(0, HammingDistance.Between(0xABCDEF, 0xABCDEF));
    }

    [Fact]
    public void Distance_counts_differing_bits()
    {
        Assert.Equal(1, HammingDistance.Between(0b0000, 0b0001));
        Assert.Equal(2, HammingDistance.Between(0b0000, 0b0011));
    }

    [Fact]
    public void MinAcrossOrientations_finds_the_closest_pair()
    {
        var a = new ulong[] { 0b1111_0000, 0b0000_1111 };
        var b = new ulong[] { 0b0000_1111 }; // matches a[1] exactly

        Assert.Equal(0, HammingDistance.MinAcrossOrientations(a, b));
    }

    [Fact]
    public void ToSimilarityPercent_maps_distance_zero_to_100_percent()
    {
        Assert.Equal(100.0, HammingDistance.ToSimilarityPercent(0));
        Assert.Equal(0.0, HammingDistance.ToSimilarityPercent(64));
    }
}
