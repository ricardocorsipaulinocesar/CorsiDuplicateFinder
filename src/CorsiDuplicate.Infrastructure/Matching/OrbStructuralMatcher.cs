using CorsiDuplicate.Core.Matching;
using OpenCvSharp;
using OpenCvSharp.Features2D;

namespace CorsiDuplicate.Infrastructure.Matching;

/// <summary>
/// ORB (Oriented FAST + Rotated BRIEF) keypoint matching + RANSAC homography check.
/// Rotation-invariant by construction and, unlike a global perceptual hash, tolerant
/// of cropping, border padding, and aspect-ratio changes — patent-free, unlike SIFT/SURF.
/// This is the most expensive signal, so it's meant to be called only for pairs where
/// cheaper signals (hash, histogram) leave the match ambiguous.
/// </summary>
public sealed class OrbStructuralMatcher : IStructuralMatcher, IDisposable
{
    private const double LoweRatioThreshold = 0.75;
    private const int MinGoodMatches = 8;

    private readonly ORB _orb = ORB.Create(500);
    private readonly Dictionary<string, OrbFeatures?> _cache = new();

    public double Match(string imagePathA, string imagePathB)
    {
        var a = GetFeatures(imagePathA);
        var b = GetFeatures(imagePathB);
        if (a is null || b is null)
        {
            return 0.0;
        }

        using var matcher = new BFMatcher(NormTypes.Hamming);
        var knnMatches = matcher.KnnMatch(a.Descriptors, b.Descriptors, 2);

        var good = new List<DMatch>();
        foreach (var pair in knnMatches)
        {
            if (pair.Length == 2 && pair[0].Distance < LoweRatioThreshold * pair[1].Distance)
            {
                good.Add(pair[0]);
            }
        }

        if (good.Count < MinGoodMatches)
        {
            return 0.0;
        }

        var srcPoints = good.Select(m => a.Keypoints[m.QueryIdx].Pt).ToArray();
        var dstPoints = good.Select(m => b.Keypoints[m.TrainIdx].Pt).ToArray();

        using var mask = new Mat();
        Cv2.FindHomography(
            InputArray.Create(srcPoints), InputArray.Create(dstPoints),
            HomographyMethods.Ransac, 5.0, mask);

        var inliers = 0;
        for (var i = 0; i < mask.Rows; i++)
        {
            if (mask.At<byte>(i, 0) != 0)
            {
                inliers++;
            }
        }

        var inlierRatio = (double)inliers / good.Count;
        // Weight by match volume too, so a couple of lucky matches on a low-detail
        // image don't produce a falsely "confident" structural match.
        var volumeWeight = Math.Min(1.0, good.Count / 30.0);
        return inlierRatio * volumeWeight * 100.0;
    }

    private OrbFeatures? GetFeatures(string path)
    {
        if (_cache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        OrbFeatures? result = null;
        try
        {
            using var img = Cv2.ImRead(path, ImreadModes.Grayscale);
            if (!img.Empty())
            {
                var descriptors = new Mat();
                _orb.DetectAndCompute(img, null, out var keypoints, descriptors);
                if (keypoints.Length > 0)
                {
                    result = new OrbFeatures(keypoints, descriptors);
                }
                else
                {
                    descriptors.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OpenCVException)
        {
            result = null;
        }

        _cache[path] = result;
        return result;
    }

    public void ClearCache()
    {
        foreach (var features in _cache.Values)
        {
            features?.Descriptors.Dispose();
        }
        _cache.Clear();
    }

    public void Dispose()
    {
        ClearCache();
        _orb.Dispose();
    }

    private sealed record OrbFeatures(KeyPoint[] Keypoints, Mat Descriptors);
}
