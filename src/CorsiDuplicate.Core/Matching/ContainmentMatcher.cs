using CorsiDuplicate.Core.Hashing;

namespace CorsiDuplicate.Core.Matching;

/// <summary>One perceptual hash per second of video. <see cref="Usable"/> is false for
/// blank/near-solid frames (fades, black frames), which must never count as a match.</summary>
public sealed record VideoFingerprint(ulong[] Hashes, bool[] Usable)
{
    public int Length => Hashes.Length;
    public int UsableCount => Usable.Count(u => u);
}

/// <summary>Where a shorter video sits inside a longer one, in whole seconds of the longer video.</summary>
public sealed record ContainmentMatch(int StartSeconds, int EndSeconds, int MatchedSeconds, double CoveragePercent);

public static class ContainmentMatcher
{
    public const int DefaultMaxDistance = 10;
    public const double DefaultMinCoveragePercent = 60;
    public const int DefaultMinMatchedSeconds = 5;

    // A "clip" this close to the source's full length is a duplicate copy, which the normal
    // duplicate sets already handle — containment is about excerpts.
    public const double MaxClipToSourceLengthRatio = 0.95;

    /// <summary>
    /// Finds whether <paramref name="clip"/> appears inside <paramref name="source"/>. Every
    /// usable clip second votes for each offset (source second − clip second) at which a
    /// similar source second exists; the most-voted offsets are then checked precisely,
    /// allowing ±1 s of drift since the two files' 1-fps sampling rarely lines up exactly.
    /// </summary>
    public static ContainmentMatch? Find(
        VideoFingerprint clip,
        VideoFingerprint source,
        int maxDistance = DefaultMaxDistance,
        double minCoveragePercent = DefaultMinCoveragePercent,
        int minMatchedSeconds = DefaultMinMatchedSeconds)
    {
        if (clip.Length == 0 || source.Length == 0 || clip.Length > source.Length * MaxClipToSourceLengthRatio)
        {
            return null;
        }

        var clipUsable = clip.UsableCount;
        if (clipUsable < minMatchedSeconds)
        {
            return null;
        }

        var votes = new Dictionary<int, int>();
        var offsetsForFrame = new HashSet<int>();
        for (var i = 0; i < clip.Length; i++)
        {
            if (!clip.Usable[i])
            {
                continue;
            }

            offsetsForFrame.Clear();
            var h = clip.Hashes[i];
            for (var j = 0; j < source.Length; j++)
            {
                if (source.Usable[j] && HammingDistance.Between(h, source.Hashes[j]) <= maxDistance)
                {
                    offsetsForFrame.Add(j - i);
                }
            }

            foreach (var offset in offsetsForFrame)
            {
                votes[offset] = votes.TryGetValue(offset, out var v) ? v + 1 : 1;
            }
        }

        if (votes.Count == 0)
        {
            return null;
        }

        ContainmentMatch? best = null;
        foreach (var offset in votes.OrderByDescending(kv => kv.Value).Take(5).Select(kv => kv.Key))
        {
            var matched = CountMatchedAt(clip, source, offset, maxDistance);
            var coverage = 100.0 * matched / clipUsable;
            if (matched < minMatchedSeconds || coverage < minCoveragePercent)
            {
                continue;
            }

            if (best is null || coverage > best.CoveragePercent)
            {
                var start = Math.Clamp(offset, 0, source.Length);
                var end = Math.Clamp(offset + clip.Length, start, source.Length);
                best = new ContainmentMatch(start, end, matched, coverage);
            }
        }

        return best;
    }

    private static int CountMatchedAt(VideoFingerprint clip, VideoFingerprint source, int offset, int maxDistance)
    {
        var matched = 0;
        for (var i = 0; i < clip.Length; i++)
        {
            if (!clip.Usable[i])
            {
                continue;
            }

            for (var j = i + offset - 1; j <= i + offset + 1; j++)
            {
                if (j >= 0 && j < source.Length && source.Usable[j]
                    && HammingDistance.Between(clip.Hashes[i], source.Hashes[j]) <= maxDistance)
                {
                    matched++;
                    break;
                }
            }
        }
        return matched;
    }
}
