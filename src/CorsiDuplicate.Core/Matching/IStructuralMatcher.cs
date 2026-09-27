namespace CorsiDuplicate.Core.Matching;

/// <summary>
/// Local-feature (ORB) structural matching: rotation-invariant and tolerant of
/// cropping/border-padding/aspect-ratio changes, unlike a global perceptual hash.
/// Expensive, so callers should only invoke this for pairs where cheaper signals
/// (hash, histogram) are ambiguous rather than for every pair.
/// </summary>
public interface IStructuralMatcher
{
    /// <summary>Returns a confidence score in [0, 100] that the two images share structural content.</summary>
    double Match(string imagePathA, string imagePathB);

    /// <summary>Drops any cached per-file feature data (call when starting a fresh scan).</summary>
    void ClearCache();
}
