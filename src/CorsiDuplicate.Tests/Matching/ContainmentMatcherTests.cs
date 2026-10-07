using CorsiDuplicate.Core.Matching;

namespace CorsiDuplicate.Tests.Matching;

public class ContainmentMatcherTests
{
    private static VideoFingerprint RandomVideo(int seconds, int seed)
    {
        var random = new Random(seed);
        var hashes = new ulong[seconds];
        for (var i = 0; i < seconds; i++)
        {
            hashes[i] = (ulong)random.NextInt64() ^ ((ulong)random.Next() << 32);
        }
        return new VideoFingerprint(hashes, Enumerable.Repeat(true, seconds).ToArray());
    }

    // Same seconds as the source, with a few bits flipped per frame (a re-encode never
    // reproduces identical hashes).
    private static VideoFingerprint Excerpt(VideoFingerprint source, int start, int length, int seed)
    {
        var random = new Random(seed);
        var hashes = new ulong[length];
        for (var i = 0; i < length; i++)
        {
            var h = source.Hashes[start + i];
            for (var flip = 0; flip < 4; flip++)
            {
                h ^= 1UL << random.Next(64);
            }
            hashes[i] = h;
        }
        return new VideoFingerprint(hashes, Enumerable.Repeat(true, length).ToArray());
    }

    [Fact]
    public void Finds_an_excerpt_and_where_it_starts()
    {
        var source = RandomVideo(600, seed: 1);
        var clip = Excerpt(source, start: 245, length: 40, seed: 2);

        var match = ContainmentMatcher.Find(clip, source);

        Assert.NotNull(match);
        Assert.Equal(245, match!.StartSeconds);
        Assert.Equal(285, match.EndSeconds);
        Assert.True(match.CoveragePercent >= 95);
    }

    [Fact]
    public void Tolerates_one_second_of_sampling_drift()
    {
        var source = RandomVideo(300, seed: 3);
        // Every other clip second lands one second later in the source.
        var hashes = Enumerable.Range(0, 30).Select(i => source.Hashes[100 + i + (i % 2)]).ToArray();
        var clip = new VideoFingerprint(hashes, Enumerable.Repeat(true, 30).ToArray());

        var match = ContainmentMatcher.Find(clip, source);

        Assert.NotNull(match);
        Assert.InRange(match!.StartSeconds, 99, 101);
    }

    [Fact]
    public void Unrelated_videos_do_not_match()
    {
        Assert.Null(ContainmentMatcher.Find(RandomVideo(40, seed: 4), RandomVideo(600, seed: 5)));
    }

    [Fact]
    public void Blank_frames_never_count_as_a_match()
    {
        var source = RandomVideo(300, seed: 6);
        var clip = Excerpt(source, start: 50, length: 30, seed: 7);
        var allBlank = new VideoFingerprint(clip.Hashes, new bool[clip.Length]);

        Assert.Null(ContainmentMatcher.Find(allBlank, source));
    }

    [Fact]
    public void A_near_full_length_copy_is_left_to_duplicate_detection()
    {
        var source = RandomVideo(100, seed: 8);
        var copy = Excerpt(source, start: 0, length: 98, seed: 9);

        Assert.Null(ContainmentMatcher.Find(copy, source));
    }
}
