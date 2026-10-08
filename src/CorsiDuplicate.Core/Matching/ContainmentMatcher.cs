using CorsiDuplicate.Core.Hashing;

namespace CorsiDuplicate.Core.Matching;

/// <summary>One perceptual hash per second of video. <see cref="Usable"/> is false for
/// blank/near-solid frames (fades, black frames), which must never count as a match.
/// <see cref="MirroredHashes"/> hashes the same frames flipped horizontally.</summary>
public sealed record VideoFingerprint(ulong[] Hashes, bool[] Usable, ulong[]? MirroredHashes = null)
{
    public int Length => Hashes.Length;
    public int UsableCount => Usable.Count(u => u);
}

/// <summary>Where a shorter video sits inside a longer one, in whole seconds of the longer video.</summary>
public sealed record ContainmentMatch(int StartSeconds, int EndSeconds, int MatchedSeconds, double CoveragePercent, bool IsMirrored = false);

/// <summary>A stretch of the clip (ClipStart–ClipEnd) that is the same footage as
/// SourceStart–SourceEnd of another video — the building block of a compilation.</summary>
public sealed record SharedSegment(int ClipStart, int ClipEnd, int SourceStart, int SourceEnd, bool IsMirrored)
{
    public int Seconds => ClipEnd - ClipStart;
}

public sealed record ContainmentComparison(ContainmentMatch? Contained, IReadOnlyList<SharedSegment> Segments);

public static class ContainmentMatcher
{
    public const int DefaultMaxDistance = 10;
    public const double DefaultMinCoveragePercent = 60;
    public const int DefaultMinMatchedSeconds = 5;
    public const int MinSegmentSeconds = 10;
    private const int MaxSegmentGapSeconds = 2;

    // A "clip" this close to the source's full length is a duplicate copy, which the normal
    // duplicate sets already handle — containment is about excerpts.
    public const double MaxClipToSourceLengthRatio = 0.95;

    // A clip second whose matches spread over more than this share of the source (still
    // camera, dark room, static title — see IsDistinctive) says little about *where* it came
    // from: it is left out of the coverage ratio and its votes are down-weighted, so two
    // different videos shot in the same place don't look like one containing the other.
    private const double NonDistinctiveShare = 0.10;

    public static ContainmentMatch? Find(VideoFingerprint clip, VideoFingerprint source) => Compare(clip, source).Contained;

    /// <summary>
    /// Aligns <paramref name="clip"/> against <paramref name="source"/>: whether the clip is an
    /// excerpt of the source (and where), and every stretch of at least
    /// <see cref="MinSegmentSeconds"/> they share. Tries the clip as-is, then mirrored.
    /// </summary>
    public static ContainmentComparison Compare(VideoFingerprint clip, VideoFingerprint source)
    {
        if (clip.Length == 0 || source.Length == 0)
        {
            return new ContainmentComparison(null, Array.Empty<SharedSegment>());
        }

        var normal = Align(clip.Hashes, clip, source, mirrored: false);
        var mirrored = clip.MirroredHashes is { } m ? Align(m, clip, source, mirrored: true) : null;

        var contained = normal.Contained ?? mirrored?.Contained;
        var segments = PickNonOverlapping(normal.Segments.Concat(mirrored?.Segments ?? Array.Empty<SharedSegment>()));
        return new ContainmentComparison(contained, segments);
    }

    private static ContainmentComparison Align(ulong[] clipHashes, VideoFingerprint clip, VideoFingerprint source, bool mirrored)
    {
        var distinctiveLimit = Math.Max(20, (int)(source.Length * NonDistinctiveShare));
        var matches = new List<int>?[clip.Length];
        var distinctive = new bool[clip.Length];
        var votes = new Dictionary<int, double>();
        var offsets = new HashSet<int>();

        for (var i = 0; i < clip.Length; i++)
        {
            if (!clip.Usable[i])
            {
                continue;
            }

            var found = new List<int>();
            var h = clipHashes[i];
            for (var j = 0; j < source.Length; j++)
            {
                if (source.Usable[j] && HammingDistance.Between(h, source.Hashes[j]) <= DefaultMaxDistance)
                {
                    found.Add(j);
                }
            }

            matches[i] = found;
            distinctive[i] = IsDistinctive(found, distinctiveLimit);
            if (found.Count == 0)
            {
                continue;
            }

            offsets.Clear();
            foreach (var j in found)
            {
                offsets.Add(j - i);
            }
            var weight = 1.0 / found.Count;
            foreach (var offset in offsets)
            {
                votes[offset] = votes.TryGetValue(offset, out var v) ? v + weight : weight;
            }
        }

        if (votes.Count == 0)
        {
            return new ContainmentComparison(null, Array.Empty<SharedSegment>());
        }

        var topOffsets = votes.OrderByDescending(kv => kv.Value).Take(8).Select(kv => kv.Key).ToList();
        return new ContainmentComparison(
            FindContained(clip, source, matches, distinctive, topOffsets, mirrored),
            FindSegments(clip, source, matches, distinctive, topOffsets, mirrored));
    }

    /// <summary>
    /// A slowly changing shot legitimately matches a run of neighbouring source seconds, so
    /// what makes a second non-distinctive is matches *spread across* the source: more than a
    /// few separate places, or one stretch wider than <paramref name="limit"/> seconds.
    /// </summary>
    private static bool IsDistinctive(List<int> found, int limit)
    {
        if (found.Count == 0)
        {
            return true;
        }

        var clusters = 1;
        var clusterStart = found[0];
        var previous = found[0];
        foreach (var j in found.Skip(1))
        {
            if (j - previous > MaxSegmentGapSeconds)
            {
                if (previous - clusterStart > limit || ++clusters > 3)
                {
                    return false;
                }
                clusterStart = j;
            }
            previous = j;
        }
        return previous - clusterStart <= limit;
    }

    private static bool MatchedAt(List<int>?[] matches, int i, int offset) =>
        matches[i] is { Count: > 0 } found && found.Any(j => Math.Abs(j - (i + offset)) <= 1);

    private static ContainmentMatch? FindContained(
        VideoFingerprint clip, VideoFingerprint source, List<int>?[] matches, bool[] distinctive,
        List<int> topOffsets, bool mirrored)
    {
        if (clip.Length > source.Length * MaxClipToSourceLengthRatio)
        {
            return null;
        }

        var distinctiveUsable = Enumerable.Range(0, clip.Length).Count(i => clip.Usable[i] && distinctive[i]);
        if (distinctiveUsable < DefaultMinMatchedSeconds)
        {
            return null;
        }

        ContainmentMatch? best = null;
        foreach (var offset in topOffsets)
        {
            var matched = Enumerable.Range(0, clip.Length).Count(i => distinctive[i] && MatchedAt(matches, i, offset));
            var coverage = 100.0 * matched / distinctiveUsable;
            if (matched < DefaultMinMatchedSeconds || coverage < DefaultMinCoveragePercent)
            {
                continue;
            }

            if (best is null || coverage > best.CoveragePercent)
            {
                var start = Math.Clamp(offset, 0, source.Length);
                var end = Math.Clamp(offset + clip.Length, start, source.Length);
                best = new ContainmentMatch(start, end, matched, coverage, mirrored);
            }
        }
        return best;
    }

    /// <summary>Runs of matched seconds along one offset; unusable/non-distinctive seconds
    /// neither break nor count toward a run, real misses may interrupt it for up to 2 s.</summary>
    private static List<SharedSegment> FindSegments(
        VideoFingerprint clip, VideoFingerprint source, List<int>?[] matches, bool[] distinctive,
        List<int> topOffsets, bool mirrored)
    {
        var segments = new List<SharedSegment>();
        foreach (var offset in topOffsets)
        {
            int? runStart = null;
            var lastMatched = -1;
            var matchedInRun = 0;
            var misses = 0;

            void Close()
            {
                if (runStart is { } s && lastMatched - s + 1 >= MinSegmentSeconds && matchedInRun >= MinSegmentSeconds / 2)
                {
                    var clipEnd = lastMatched + 1;
                    segments.Add(new SharedSegment(s, clipEnd,
                        Math.Clamp(s + offset, 0, source.Length), Math.Clamp(clipEnd + offset, 0, source.Length), mirrored));
                }
                runStart = null;
                matchedInRun = 0;
                misses = 0;
            }

            for (var i = 0; i < clip.Length; i++)
            {
                if (!clip.Usable[i] || !distinctive[i])
                {
                    continue;
                }

                if (MatchedAt(matches, i, offset))
                {
                    runStart ??= i;
                    lastMatched = i;
                    matchedInRun++;
                    misses = 0;
                }
                else if (runStart is not null && ++misses > MaxSegmentGapSeconds)
                {
                    Close();
                }
            }
            Close();
        }
        return segments;
    }

    private static IReadOnlyList<SharedSegment> PickNonOverlapping(IEnumerable<SharedSegment> candidates)
    {
        var picked = new List<SharedSegment>();
        foreach (var segment in candidates.OrderByDescending(s => s.Seconds))
        {
            if (picked.All(p => segment.ClipEnd <= p.ClipStart || segment.ClipStart >= p.ClipEnd))
            {
                picked.Add(segment);
            }
        }
        return picked.OrderBy(s => s.ClipStart).ToList();
    }
}
